//! Prelisted fake-I/O installed dispatcher tests. Every root, SID, authority,
//! metadata sample and native port is injected before a default resolver exists.
use super::*;
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[path = "driver_mode_owner.rs"]
mod driver_mode_owner;
#[cfg(feature = "service-keyboard-driver-mode-v1")]
#[path = "driver_mode_record_model.rs"]
mod driver_mode_record_model;
#[cfg(feature = "service-gpu-clock-tuning-v1")]
#[path = "gpu_clock_tuning.rs"]
mod gpu_clock_tuning;
#[cfg(feature = "service-lighting-v1")]
#[path = "lighting_override.rs"]
mod lighting_override;
#[cfg(feature = "service-performance-shortcut-v1")]
#[path = "performance_shortcut.rs"]
mod performance_shortcut;
#[cfg(feature = "service-fan-mutation-v2")]
#[path = "pool_pins.rs"]
mod pool_pins;
#[path = "pool_seed.rs"]
mod pool_seed;
#[path = "profile_layer_lighting.rs"]
mod profile_layer_lighting;
#[path = "read_worker_restart.rs"]
mod read_worker_restart;
#[path = "temporary_charge_corrections.rs"]
mod tfc_corrections;
#[cfg(feature = "service-temporary-charge-v1")]
#[path = "temporary_charge_release.rs"]
mod tfc_release;
#[path = "thermal_presets.rs"]
mod thermal_presets;
#[path = "unclassified_external.rs"]
mod unclassified_external;
use crate::discovery::performance::{ReadAdmission, mutation_fixture::Fixture};
#[cfg(feature = "service-fan-mutation-v2")]
use crate::installed::readiness::thermal_mutation::Cleanup;
#[cfg(feature = "service-hyperboost-v1")]
use crate::installed::readiness::thermal_mutation::{Pad, Probes};
use crate::installed::readiness::thermal_storage::{Files, Storage};
use crate::installed::readiness::{
    mailbox_read::{CleanupScope, MailboxSource, MutationFactories, ReadCustody, ReadSource},
    status_source::{StatusOrigin, injected_mutation_source},
    thermal_mutation::{Fresh, Runtime, Settle},
};
use std::{
    collections::BTreeMap,
    sync::{
        Arc, Mutex as StdMutex,
        atomic::{AtomicBool, AtomicU8, AtomicUsize, Ordering},
    },
};
// Test-only provenance; raw stays a string to preserve encoder property order.
/// `Storage::desired` over the profile layer read now, with no foreground
/// application, as each request step reads it.
fn saved_desired(
    storage: &Storage,
    power: i32,
) -> io::Result<Option<(openblade_domain::thermal_mutation::Destination, i32)>> {
    let layer = storage.profile_layer(&Default::default())?;
    storage.desired(power, layer.at(power)?.as_ref())
}
/// `Storage::desired_fan` over the profile layer read now, as `saved_desired`.
fn saved_fan(storage: &Storage, power: i32) -> io::Result<Option<Value>> {
    let layer = storage.profile_layer(&Default::default())?;
    storage.desired_fan(power, layer.at(power)?.as_ref())
}
fn emit_fixture_frame(emitter: &str, operation: &str, method: &str, raw: &[u8]) {
    assert!(matches!(
        emitter,
        "peer_response" | "peer_subscription" | "publisher_projection" | "synthetic_projection"
    ));
    assert!(matches!(method, "response" | "statusEvent"));
    assert!(!operation.is_empty());
    println!(
        "OPENBLADE_FIXTURE_FRAME {}",
        json!({
            "schemaVersion": 1,
            "emitter": emitter,
            "operation": operation,
            "method": method,
            "raw": std::str::from_utf8(raw).unwrap(),
        })
    );
}
const SID: &str = "S-1-5-21-111-222-333-1001";
const USER: Caller = Caller {
    pid: Some(42),
    logon: Logon::Interactive,
    session: 7,
    control_session: Some(7),
};
#[derive(Clone, Default)]
struct MemoryFiles {
    bytes: Arc<StdMutex<BTreeMap<String, Vec<u8>>>>,
    fail: Arc<AtomicBool>,
    capacity_refuse: Arc<AtomicBool>,
    last_reservation: Arc<AtomicUsize>,
    reserve_uncertain: Arc<AtomicBool>,
    roll_settled: Arc<AtomicBool>,
    fail_quota_read: Arc<AtomicBool>,
    // Fails the next read of the saved preferences once: 1 with an
    // ordinary I/O error, 2 with fatal admission evidence.
    #[cfg(feature = "service-profile-triggers-v1")]
    fail_preferences_read: Arc<AtomicU8>,
    // Counts the reads of the saved preferences.
    #[cfg(feature = "service-profile-triggers-v1")]
    preferences_reads: Arc<AtomicUsize>,
    release_fault: Arc<AtomicU8>,
    release_reads: Arc<AtomicUsize>,
    release_read_notify: Arc<tokio::sync::Notify>,
    fail_terminal: Arc<AtomicBool>,
    fail_battery_settled: Arc<AtomicBool>,
    fail_prepared: Arc<AtomicU8>,
    fail_first_intent: Arc<AtomicU8>,
    first_intent_bytes: Arc<StdMutex<Option<Vec<u8>>>>,
    prepared_attempts: Arc<AtomicUsize>,
    submission_intent_attempts: Arc<AtomicUsize>,
    prepared_bytes: Arc<StdMutex<Option<Vec<u8>>>>,
    fatal_terminal: Arc<AtomicBool>,
    fatal_first_intent: Arc<AtomicBool>,
    first_intent_attempts: Arc<AtomicUsize>,
    first_intent_prior: Arc<StdMutex<Option<Vec<u8>>>>,
    terminal_attempts: Arc<AtomicUsize>,
    /// A file whose replacement blocks for this long, as slow storage does.
    stalled_replace: Arc<StdMutex<Option<(&'static str, std::time::Duration)>>>,
    /// A file whose every replacement on a fixture job takes this long on the
    /// job's paused clock. Elsewhere it takes no time.
    paced_replace: Arc<StdMutex<Option<(&'static str, std::time::Duration)>>>,
}
impl MemoryFiles {
    fn put(&self, name: &str, value: Value) {
        self.bytes
            .lock()
            .unwrap()
            .insert(name.into(), serde_json::to_vec(&value).unwrap());
    }
    fn document(&self, name: &str) -> Value {
        serde_json::from_slice(self.bytes.lock().unwrap().get(name).unwrap()).unwrap()
    }
    fn profiles(&self, performance: i32) {
        self.profiles_with(performance, None);
    }
    /// `usb_c` replaces the fixture's limited-adapter preset (Balanced).
    fn profiles_with(&self, performance: i32, usb_c: Option<i32>) {
        self.profiles_document(performance, usb_c, Value::Null);
    }
    /// The plugged-in profile also carries a GPU clock effect, which has no
    /// native pair.
    fn profiles_gpu(&self, performance: i32) {
        self.profiles_document(
            performance,
            None,
            json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}}),
        );
    }
    fn profiles_document(&self, performance: i32, usb_c: Option<i32>, plugged_in_gpu: Value) {
        let fixture:Value=serde_json::from_str(include_str!("../../../../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json")).unwrap();
        let profile: Value = serde_json::from_str(fixture["prior"][0].as_str().unwrap()).unwrap();
        let mut profiles = profile["profiles"].clone();
        profiles["pluggedIn"]["performance"] = json!(performance);
        if let Some(usb_c) = usb_c {
            profiles["usbC"]["performance"] = json!(usb_c);
        }
        profiles["pluggedIn"]["gpuClockTuning"] = plugged_in_gpu;
        profiles["applicationProfiles"] = json!([{ "id":"11111111-2222-3333-4444-555555555555","name":"Game","executablePath":"C:\\Games\\Game.exe","activationMode":0,"enabled":true,"overrides":{"pluggedIn":{"performance":2}}}]);
        self.put(
            "profiles.json",
            json!({"schemaVersion":crate::configuration::CURRENT_SCHEMA,"profiles":profiles}),
        );
        self.put(
            "battery-preferences.json",
            serde_json::from_str(fixture["prior"][1].as_str().unwrap()).unwrap(),
        );
        self.commit_configuration();
    }
    /// A plugged-in Custom profile whose application profile carries `app` as
    /// its plugged-in overrides.
    fn profiles_custom(&self, app: Value) {
        self.profiles(5);
        let mut document = self.document("profiles.json");
        document["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"] = app;
        self.put("profiles.json", document);
        self.commit_configuration();
    }
    fn commit_configuration(&self) {
        let bytes = self.bytes.lock().unwrap();
        let profile = crate::configuration::digest_hex(&bytes["profiles.json"]);
        let prefs = crate::configuration::digest_hex(&bytes["battery-preferences.json"]);
        drop(bytes);
        self.put(
            "battery-configuration.commit.json",
            json!({"schemaVersion":crate::configuration::CURRENT_SCHEMA,"profileSha256":profile,"preferencesSha256":prefs}),
        );
    }
}
impl Files for MemoryFiles {
    fn read_release(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        if name == "thermal-mutation.journal.pending" {
            self.release_reads.fetch_add(1, Ordering::AcqRel);
            self.release_read_notify.notify_one();
            match self.release_fault.load(Ordering::Acquire) {
                1 => {
                    return Err(
                        crate::installed::readiness::thermal_storage::release_open_error(
                            io::Error::from_raw_os_error(32),
                        ),
                    );
                }
                2 => {
                    return Err(
                        crate::installed::readiness::thermal_mutation::fatal_admission(
                            "injected protected release trust failure",
                        ),
                    );
                }
                4 => return Err(io::Error::other("unclassified release read")),
                5 => return Err(io::Error::from_raw_os_error(32)),
                _ => {}
            }
        }
        if name == "thermal-mutation.journal" && self.release_fault.load(Ordering::Acquire) == 3 {
            return Ok(Some(b"mismatched journal".to_vec()));
        }
        self.read(name)
    }
    fn reserve(&self, expected: Option<&[u8]>, needed: usize) -> io::Result<Option<Vec<u8>>> {
        self.last_reservation.store(needed, Ordering::Release);
        if self.reserve_uncertain.load(Ordering::Acquire) {
            return Err(io::Error::other("injected uncertain reservation I/O"));
        }
        if self.capacity_refuse.load(Ordering::Acquire) {
            return Err(crate::thermal_history::capacity_refusal());
        }
        if self.roll_settled.load(Ordering::Acquire)
            && needed <= crate::thermal_history::SEGMENT
            && expected.is_some_and(|b| b.len() + needed > crate::thermal_history::SEGMENT)
            && crate::thermal_history::handback_complete(expected.unwrap())?
        {
            let mut files = self.bytes.lock().unwrap();
            assert_eq!(
                files.get("thermal-mutation.journal").map(Vec::as_slice),
                expected
            );
            let old = files.remove("thermal-mutation.journal").unwrap();
            assert!(files.insert("retained-segment".into(), old).is_none());
            return Ok(None);
        }
        if self.capacity_refuse.load(Ordering::Acquire)
            || needed > crate::thermal_history::SEGMENT
            || expected
                .unwrap_or_default()
                .len()
                .checked_add(needed)
                .is_none_or(|n| n > crate::thermal_history::SEGMENT)
        {
            return Err(io::Error::new(
                io::ErrorKind::StorageFull,
                "Thermal operation progress capacity unavailable before Prepared.",
            ));
        }
        Ok(expected.map(<[u8]>::to_vec))
    }
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        if name == "thermal-mutation.journal.pending"
            && self.fail_quota_read.load(Ordering::Acquire)
        {
            return Err(io::Error::other("injected quota read failure"));
        }
        #[cfg(feature = "service-profile-triggers-v1")]
        if name == "battery-preferences.json" {
            self.preferences_reads.fetch_add(1, Ordering::AcqRel);
            match self.fail_preferences_read.swap(0, Ordering::AcqRel) {
                0 => {}
                1 => return Err(io::Error::other("injected preferences read failure")),
                _ => {
                    return Err(
                        crate::installed::readiness::thermal_mutation::fatal_admission(
                            "injected fatal preferences read",
                        ),
                    );
                }
            }
        }
        Ok(self.bytes.lock().unwrap().get(name).cloned())
    }
    #[cfg(feature = "service-profiles-v1")]
    fn remove(&self, name: &str, expected: &[u8]) -> io::Result<()> {
        let mut files = self.bytes.lock().unwrap();
        if files.get(name).map(Vec::as_slice) != Some(expected) {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        files.remove(name);
        Ok(())
    }
    #[cfg(feature = "service-profiles-v1")]
    fn discard_stage(&self, _name: &str) -> io::Result<()> {
        // This store replaces in one step and never stages.
        Ok(())
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        let stalled = self
            .stalled_replace
            .lock()
            .unwrap()
            .take_if(|(file, _)| *file == name);
        if let Some((_, time)) = stalled {
            std::thread::sleep(time);
        }
        let paced = *self.paced_replace.lock().unwrap();
        if let Some((file, time)) = paced
            && file == name
            && std::thread::current().name() == Some("openblade-firmware-observer")
        {
            // A synchronous append spends paused time by polling one advance.
            let mut advance = std::pin::pin!(tokio::time::advance(time));
            let _ = advance
                .as_mut()
                .poll(&mut std::task::Context::from_waker(std::task::Waker::noop()));
        }
        if name == "thermal-mutation.journal" {
            let progress = bytes
                .split(|b| *b == b'\n')
                .rfind(|line| !line.is_empty())
                .map(|line| {
                    serde_json::from_slice::<Value>(line).unwrap()["progress"]
                        .as_u64()
                        .unwrap()
                });
            // A HyperBoost exit to a battery preset journals its first submission
            // intent as 80 (the restore pair).
            if progress == Some(1) || progress == Some(60) || progress == Some(80) {
                self.submission_intent_attempts
                    .fetch_add(1, Ordering::AcqRel);
                let cut = self.fail_first_intent.load(Ordering::Acquire);
                if cut != 0 {
                    let mut files = self.bytes.lock().unwrap();
                    let prior = files.get(name).cloned().unwrap();
                    assert_eq!(Some(prior.as_slice()), expected);
                    assert!(bytes.starts_with(&prior));
                    assert!(!files.contains_key("thermal-mutation.journal.pending"));
                    *self.first_intent_prior.lock().unwrap() = Some(prior.clone());
                    *self.first_intent_bytes.lock().unwrap() = Some(bytes.to_vec());
                    self.first_intent_attempts.fetch_add(1, Ordering::AcqRel);
                    match cut {
                        1 => {
                            let partial = prior.len() + (bytes.len() - prior.len()) / 2;
                            files.insert(
                                "thermal-mutation.journal.pending".into(),
                                bytes[..partial].to_vec(),
                            );
                        }
                        2 => {
                            files.insert("thermal-mutation.journal.pending".into(), bytes.to_vec());
                        }
                        3 => {
                            files.insert(name.into(), bytes.to_vec());
                        } // published, final sync/readback failed
                        _ => panic!("unknown FirstIntent I/O cut"),
                    }
                    return Err(io::Error::new(
                        io::ErrorKind::StorageFull,
                        "injected ordinary FirstIntent persistence failure",
                    ));
                }
            }
            if progress == Some(0) {
                self.prepared_attempts.fetch_add(1, Ordering::AcqRel);
                let cut = self.fail_prepared.load(Ordering::Acquire);
                if cut != 0 {
                    let mut files = self.bytes.lock().unwrap();
                    assert_eq!(files.get(name).map(Vec::as_slice), expected);
                    // Both thermal and battery requests reserve before Prepared.
                    *self.prepared_bytes.lock().unwrap() = Some(bytes.to_vec());
                    match cut {
                        1 => {} // ordinary staging-open failure
                        2 => {
                            files.insert(
                                "thermal-mutation.journal.pending".into(),
                                bytes[..bytes.len() / 2].to_vec(),
                            );
                        }
                        3 => {
                            files.insert("thermal-mutation.journal.pending".into(), bytes.to_vec());
                        }
                        4 => {
                            files.insert(name.into(), bytes.to_vec());
                        } // published, final sync/readback failed
                        5 => {
                            return Err(
                                crate::installed::readiness::thermal_mutation::fatal_admission(
                                    "injected Prepared protection failure",
                                ),
                            );
                        }
                        _ => panic!("unknown Prepared I/O cut"),
                    }
                    return Err(io::Error::new(
                        io::ErrorKind::StorageFull,
                        "injected ordinary Prepared persistence failure",
                    ));
                }
            }
        }
        if name == "thermal-mutation.journal"
            && self.fatal_first_intent.load(Ordering::Acquire)
            && bytes
                .split(|b| *b == b'\n')
                .rfind(|line| !line.is_empty())
                .is_some_and(|line| serde_json::from_slice::<Value>(line).unwrap()["progress"] == 1)
        {
            let prior = self.bytes.lock().unwrap().get(name).cloned().unwrap();
            assert_eq!(Some(prior.as_slice()), expected);
            assert_eq!(
                serde_json::from_slice::<Value>(prior.strip_suffix(b"\n").unwrap()).unwrap()["progress"],
                0
            );
            *self.first_intent_prior.lock().unwrap() = Some(prior);
            self.first_intent_attempts.fetch_add(1, Ordering::AcqRel);
            return Err(
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "injected FirstIntent staging protection failure",
                ),
            );
        }
        if name == "thermal-mutation.journal"
            && bytes
                .split(|b| *b == b'\n')
                .rfind(|line| !line.is_empty())
                .is_some_and(|line| serde_json::from_slice::<Value>(line).unwrap()["progress"] == 6)
        {
            self.terminal_attempts.fetch_add(1, Ordering::AcqRel);
            if self.fatal_terminal.load(Ordering::Acquire) {
                return Err(
                    crate::installed::readiness::thermal_mutation::fatal_admission(
                        "injected terminal staging protection failure",
                    ),
                );
            }
            if self.fail_terminal.load(Ordering::Acquire) {
                self.bytes
                    .lock()
                    .unwrap()
                    .insert("thermal-mutation.journal.pending".into(), bytes.to_vec());
                return Err(io::ErrorKind::PermissionDenied.into());
            }
        }
        if name == "thermal-mutation.journal"
            && self.fail_battery_settled.load(Ordering::Acquire)
            && bytes
                .split(|b| *b == b'\n')
                .rfind(|line| !line.is_empty())
                .is_some_and(|line| {
                    matches!(
                        serde_json::from_slice::<Value>(line).unwrap()["progress"].as_u64(),
                        Some(62 | 63 | 72 | 73)
                    )
                })
        {
            return Err(io::Error::other(
                "injected battery settlement publication failure",
            ));
        }
        if self.fail.load(Ordering::Acquire) {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        let mut files = self.bytes.lock().unwrap();
        if files.get(name).map(Vec::as_slice) != expected {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        files.insert(name.into(), bytes.into());
        Ok(())
    }
}
/// The rig storage's journal as production's `CustodialFiles` reaches it
/// through `ReadLease`: a reservation or plain append needs the custody's
/// ordinary authorization, which reads the lifecycle epoch and so refuses
/// while the low-power gate is set; a cleanup append needs the owned-stop
/// authorization; a Prepared retirement needs only intact custody, no pending
/// stage and exactly the Prepared row settled as 6 under the lease's own
/// owner. Production binds that owner when the lease opens, so a later
/// change of `Custody::generation`, which models a fresh-proof owner change,
/// does not change it. Other files pass through.
#[derive(Clone)]
struct CustodialMemory {
    files: MemoryFiles,
    custody: Arc<Custody>,
    owner: String,
}
impl CustodialMemory {
    fn new(files: MemoryFiles, custody: Arc<Custody>) -> Self {
        use sha2::Digest;
        let generation = [custody.generation.load(Ordering::Acquire); 32];
        let owner = format!("{:x}", sha2::Sha256::digest(generation));
        Self {
            files,
            custody,
            owner,
        }
    }
}
const CUSTODY_JOURNAL: &str = "thermal-mutation.journal";
fn custody_admits(admitted: bool) -> io::Result<()> {
    admitted
        .then_some(())
        .ok_or_else(|| io::ErrorKind::PermissionDenied.into())
}
impl Files for CustodialMemory {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        self.files.read(name)
    }
    fn read_release(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        self.files.read_release(name)
    }
    fn reserve(&self, expected: Option<&[u8]>, needed: usize) -> io::Result<Option<Vec<u8>>> {
        custody_admits(ReadCustody::authorize(&*self.custody).is_some())?;
        self.files.reserve(expected, needed)
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if name == CUSTODY_JOURNAL {
            custody_admits(ReadCustody::authorize(&*self.custody).is_some())?;
        }
        self.files.replace(name, expected, bytes)
    }
    fn replace_cleanup(
        &self,
        name: &str,
        scope: CleanupScope,
        expected: Option<&[u8]>,
        bytes: &[u8],
    ) -> io::Result<()> {
        custody_admits(
            name == CUSTODY_JOURNAL
                && ReadCustody::authorize_cleanup(&*self.custody, scope).is_some(),
        )?;
        self.files.replace(name, expected, bytes)
    }
    fn replace_retired(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        custody_admits(name == CUSTODY_JOURNAL)?;
        self.custody.verify_integrity()?;
        crate::thermal_history::validate_prepared_retirement(
            expected.ok_or(io::ErrorKind::PermissionDenied)?,
            bytes,
            &self.owner,
        )?;
        custody_admits(
            self.files
                .read("thermal-mutation.journal.pending")?
                .is_none(),
        )?;
        self.files.replace(name, expected, bytes)
    }
    #[cfg(feature = "service-profiles-v1")]
    fn remove(&self, name: &str, expected: &[u8]) -> io::Result<()> {
        self.files.remove(name, expected)
    }
    #[cfg(feature = "service-profiles-v1")]
    fn discard_stage(&self, name: &str) -> io::Result<()> {
        self.files.discard_stage(name)
    }
}
struct Custody {
    me: std::sync::Weak<Custody>,
    current_sid: StdMutex<String>,
    current_session: AtomicUsize,
    current_console: AtomicBool,
    scoped_fresh: AtomicBool,
    // The next write captures pin the lifecycle epoch, as `WindowsGuard`
    // does (`PinnedFresh`).
    pinned_fresh: AtomicBool,
    // The next captures open a second Storage over the same files, as a
    // per-user cache eviction would.
    other_storage: AtomicBool,
    generation: AtomicU8,
    authentication: AtomicUsize,
    live: AtomicBool,
    origin_for_fan: StdMutex<Option<Arc<crate::origin_lifetime::Lifetime>>>,
    authority_for_fan: AtomicU8,
    fresh_refuse: AtomicBool,
    // A competing controller present at the next write captures: the factory
    // refuses them with the typed `competing_controller` error, as
    // `WindowsGuard::capture` does, and every fresh authorization refuses.
    controller: AtomicBool,
    fatal_health: AtomicBool,
    integrity_lost: AtomicBool,
    // Loses integrity at the nth check once armed (0 is off).
    integrity_lost_at: AtomicUsize,
    // Refuses the nth ordinary read authorization from now, once (0 is
    // off): a read admission lost between two checks of one step.
    read_refuse_at: AtomicUsize,
    // Blocks the thread in the nth owned-Stop fresh authorization once
    // armed, as a slow Windows supplier would: on the clock of the runtime
    // that asks (`Custody::stall_owned_stop`).
    owned_stop_stall: StdMutex<Option<(usize, std::time::Duration)>>,
    activation_refusal: AtomicU8,
    lifecycle: crate::scm_host::Lifecycle,
    activations: AtomicUsize,
    retired: AtomicBool,
    // One SCM notice delivered inside a later fresh authorization, after the
    // caller read its epoch: the race a second power notice can win. It
    // lands in the authorization after `notice_skip` others have passed.
    notice_skip: AtomicUsize,
    // A competing controller that appears in the `refuse_at`th fresh
    // authorization and stays (0: never), at an unchanged epoch.
    refuse_at: AtomicUsize,
    notice_during_authorize: StdMutex<
        Option<(
            u32,
            u32,
            Option<openblade_domain::power_setting::PowerSetting>,
        )>,
    >,
    port: Arc<StdMutex<crate::discovery::performance::mutation_fixture::State>>,
    // The files whose journal `journal_read_release` returns, as production's
    // `ReadLease` reads it (`Rig::restarted`), once each of
    // `journal_failures` has been returned. Without files it refuses.
    journal: StdMutex<Option<MemoryFiles>>,
    journal_failures: StdMutex<std::collections::VecDeque<io::Error>>,
    // The keyboard Driver-mode record in the rig's files.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    driver_mode: driver_mode_record_model::RecordModel,
}
impl Custody {
    fn new(port: &Fixture) -> Arc<Self> {
        Arc::new_cyclic(|me| Self {
            journal: StdMutex::new(None),
            journal_failures: StdMutex::default(),
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            driver_mode: Default::default(),
            me: me.clone(),
            current_sid: StdMutex::new(SID.into()),
            current_session: AtomicUsize::new(7),
            current_console: AtomicBool::new(true),
            scoped_fresh: AtomicBool::new(false),
            pinned_fresh: AtomicBool::new(false),
            other_storage: AtomicBool::new(false),
            generation: AtomicU8::new(7),
            authentication: AtomicUsize::new(1),
            port: port.state.clone(),
            live: AtomicBool::new(true),
            origin_for_fan: StdMutex::new(None),
            authority_for_fan: AtomicU8::new(0),
            fresh_refuse: AtomicBool::new(false),
            controller: AtomicBool::new(false),
            fatal_health: AtomicBool::new(false),
            integrity_lost: AtomicBool::new(false),
            integrity_lost_at: AtomicUsize::new(0),
            read_refuse_at: AtomicUsize::new(0),
            owned_stop_stall: StdMutex::new(None),
            activation_refusal: AtomicU8::new(0),
            lifecycle: Default::default(),
            activations: AtomicUsize::new(0),
            retired: AtomicBool::new(false),
            notice_skip: AtomicUsize::new(0),
            refuse_at: AtomicUsize::new(0),
            notice_during_authorize: StdMutex::new(None),
        })
    }
    /// Whether this lease may write the Driver-mode record: it activated
    /// mutation and is not recovery-only, as the owner Store requires.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn writable(&self) -> bool {
        self.activations.load(Ordering::Acquire) > 0 && !ReadCustody::recovery_only(self)
    }
}
impl ReadCustody for Custody {
    fn journal_read_release(&self, pending: bool) -> io::Result<Option<Vec<u8>>> {
        if let Some(failure) = self.journal_failures.lock().unwrap().pop_front() {
            return Err(failure);
        }
        let files = self.journal.lock().unwrap().clone();
        files
            .ok_or(io::ErrorKind::PermissionDenied)?
            .read(if pending {
                "thermal-mutation.journal.pending"
            } else {
                CUSTODY_JOURNAL
            })
    }
    // The Driver-mode record the files hold, as the owner custody keeps it
    // (`driver_mode_record_model`). Only an activated, writable lease has a
    // writer, and each replace holds its caller's admission.
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_read(
        &self,
    ) -> io::Result<Option<openblade_domain::driver_mode_history::Record>> {
        self.driver_mode.read()
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_owner(&self) -> io::Result<String> {
        self.driver_mode.owner(self.writable())
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace(
        &self,
        expected: Option<&openblade_domain::driver_mode_history::Record>,
        next: &openblade_domain::driver_mode_history::Record,
    ) -> io::Result<()> {
        let admitted = ReadCustody::authorize(self).is_some();
        self.driver_mode
            .replace(self.writable(), admitted, None, expected, next)
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn driver_mode_record_replace_cleanup(
        &self,
        scope: CleanupScope,
        expected: Option<&openblade_domain::driver_mode_history::Record>,
        next: &openblade_domain::driver_mode_history::Record,
    ) -> io::Result<()> {
        let admitted = ReadCustody::authorize_cleanup(self, scope).is_some();
        self.driver_mode
            .replace(self.writable(), admitted, Some(scope), expected, next)
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        if !self.live.load(Ordering::Acquire) || self.authority_for_fan.load(Ordering::Acquire) != 0
        {
            return None;
        }
        if let Some(origin) = &*self.origin_for_fan.lock().unwrap() {
            origin.verify_owned_stop().ok()?;
        }
        Some(ReadAdmission {
            lifecycle: self.lifecycle.active()?,
            generation: [self.generation.load(Ordering::Acquire); 32],
        })
    }
    // `ReadLease`'s low-power scope: `authorize` at the suspend generation's
    // lifecycle fence (`Lifecycle::fence_for`).
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn authorize_cleanup(&self, scope: CleanupScope) -> Option<ReadAdmission> {
        let CleanupScope::LowPower { generation } = scope else {
            return ReadCustody::authorize_owned_stop(self);
        };
        if !self.live.load(Ordering::Acquire) || self.authority_for_fan.load(Ordering::Acquire) != 0
        {
            return None;
        }
        if let Some(origin) = &*self.origin_for_fan.lock().unwrap() {
            origin.verify().ok()?;
        }
        let write = openblade_domain::low_power_gate::WriteScope::LowPowerCleanup { generation };
        Some(ReadAdmission {
            lifecycle: self.lifecycle.fence_for(write)?,
            generation: [self.generation.load(Ordering::Acquire); 32],
        })
    }
    fn verify_integrity(&self) -> io::Result<()> {
        if self
            .integrity_lost_at
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| n.checked_sub(1))
            == Ok(1)
        {
            self.integrity_lost.store(true, Ordering::Release);
        }
        if self.integrity_lost.load(Ordering::Acquire) {
            Err(
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "injected original owner integrity lost",
                ),
            )
        } else {
            Ok(())
        }
    }
    // `authorize` without the lifecycle epoch, as `ReadLease` models it.
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        let generation = [self.generation.load(Ordering::Acquire); 32];
        if let Some(origin) = &*self.origin_for_fan.lock().unwrap() {
            return crate::installed::readiness::mailbox_read::authorize_original(
                &self.authority_for_fan,
                origin.verify().map(|_| true),
                || origin.verify(),
                ReadAdmission {
                    generation,
                    lifecycle: 0,
                },
            )
            .is_some();
        }
        self.live.load(Ordering::Acquire)
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        if self
            .read_refuse_at
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| n.checked_sub(1))
            == Ok(1)
        {
            return None;
        }
        if let Some(origin) = &*self.origin_for_fan.lock().unwrap() {
            let proof = ReadAdmission {
                lifecycle: self.lifecycle.active()?,
                generation: [self.generation.load(Ordering::Acquire); 32],
            };
            return crate::installed::readiness::mailbox_read::authorize_original(
                &self.authority_for_fan,
                origin.verify().map(|_| true),
                || origin.verify(),
                proof,
            );
        }
        self.live.load(Ordering::Acquire).then_some(ReadAdmission {
            lifecycle: self.lifecycle.active()?,
            generation: [self.generation.load(Ordering::Acquire); 32],
        })
    }
    fn activate_mutation(
        &self,
    ) -> Result<(), crate::installed::readiness::mailbox_read::ActivationFailure> {
        ReadCustody::authorize(self).ok_or_else(|| {
            crate::installed::readiness::mailbox_read::ActivationFailure::KnownUnpublished(
                io::ErrorKind::PermissionDenied.into(),
            )
        })?;
        let port = self.port.lock().unwrap();
        assert_eq!(
            port.opened, port.retired,
            "activation before actual fake native port retirement"
        );
        drop(port);
        match self.activation_refusal.load(Ordering::Acquire) {
            // 3 is a recovery-only lease, which refuses every activation.
            1 | 3 => {
                return Err(
                    crate::installed::readiness::mailbox_read::ActivationFailure::KnownUnpublished(
                        io::ErrorKind::PermissionDenied.into(),
                    ),
                );
            }
            2 => {
                return Err(
                    crate::installed::readiness::mailbox_read::ActivationFailure::Uncertain(
                        io::ErrorKind::PermissionDenied.into(),
                    ),
                );
            }
            _ => {}
        }
        assert_eq!(self.activations.fetch_add(1, Ordering::AcqRel), 0);
        Ok(())
    }
    #[cfg(feature = "service-keyboard-driver-mode-v1")]
    fn recovery_only(&self) -> bool {
        self.activation_refusal.load(Ordering::Acquire) == 3
    }
    fn revoke(&self) {
        self.live.store(false, Ordering::Release);
    }
    fn retire(&self) -> io::Result<()> {
        self.retired.store(true, Ordering::Release);
        Ok(())
    }
    fn confirm_drained(&self) -> io::Result<()> {
        if self.retired.load(Ordering::Acquire) {
            Ok(())
        } else {
            Err(io::ErrorKind::WouldBlock.into())
        }
    }
}
impl Fresh for Custody {
    fn on_battery(&self) -> Option<bool> {
        let port = self.port.lock().unwrap();
        port.windows_on_battery.unwrap_or(Some(port.actual == 0))
    }
    // The same kind mapping as `WindowsGuard::power_generation`; the SCM
    // power-notice sample is `Rig::notice` (unclassified_external.rs).
    fn power_generation(&self) -> Option<u64> {
        // Unknown Windows power is a failed capture, not a kind.
        let kind = self.on_battery().map(|battery| if battery { 1 } else { 2 });
        self.lifecycle.observe_power_kind(kind)
    }
    // `usize::MAX` models a capture without a logon identity.
    fn authentication(&self) -> Option<(u32, i32)> {
        let authentication = self.authentication.load(Ordering::Acquire);
        (authentication != usize::MAX).then_some((authentication as u32, 0))
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.stall_owned_stop();
        self.verify_health().ok()?;
        if self.fresh_refuse.load(Ordering::Acquire) || self.controller.load(Ordering::Acquire) {
            return None;
        }
        ReadCustody::authorize_owned_stop(self)
    }
    fn verify_health(&self) -> io::Result<()> {
        self.verify_integrity()?;
        if self.fatal_health.load(Ordering::Acquire) {
            return Err(
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "injected controller admission lock poisoned",
                ),
            );
        }
        self.lifecycle.verify_health()
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        let notice = {
            let mut armed = self.notice_during_authorize.lock().unwrap();
            let skipped = armed.is_some()
                && self
                    .notice_skip
                    .try_update(Ordering::AcqRel, Ordering::Acquire, |n| n.checked_sub(1))
                    .is_ok();
            if skipped { None } else { armed.take() }
        };
        if let Some((code, event, setting)) = notice {
            self.lifecycle.notice(code, event, setting);
        }
        let counted = self
            .refuse_at
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| n.checked_sub(1));
        if counted == Ok(1) {
            self.fresh_refuse.store(true, Ordering::Release);
        }
        if self.fresh_refuse.load(Ordering::Acquire)
            || self.controller.load(Ordering::Acquire)
            || self.fatal_health.load(Ordering::Acquire)
        {
            return None;
        }
        ReadCustody::authorize(self)
    }
    // `authorize` without the lifecycle epoch: `fresh_refuse` stands for
    // every refused Windows supplier, a competing controller included, and
    // an unknown Windows AC line status refuses as `Identity::admits` does.
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        !self.fresh_refuse.load(Ordering::Acquire)
            && !self.controller.load(Ordering::Acquire)
            && !self.fatal_health.load(Ordering::Acquire)
            && Fresh::on_battery(self).is_some()
            && ReadCustody::authorize_storage(self)
    }
    fn capture_settle(
        &self,
        sid: &str,
        session: u32,
        authentication: Option<(u32, i32)>,
    ) -> io::Result<Arc<dyn Settle>> {
        Fresh::verify_health(self)?;
        let fresh = SettleFresh {
            custody: self.me.upgrade().ok_or(io::ErrorKind::PermissionDenied)?,
            sid: sid.into(),
            session,
            authentication,
            epoch: self
                .lifecycle
                .active()
                .ok_or(io::ErrorKind::PermissionDenied)?,
        };
        fresh
            .authorize_owned_stop()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        Ok(Arc::new(fresh))
    }
}
/// Test model of `WindowsGuard::capture_settle`: the owner's SID, session and
/// logon, checked against the current ones, at the epoch current at capture.
struct SettleFresh {
    custody: Arc<Custody>,
    sid: String,
    session: u32,
    authentication: Option<(u32, i32)>,
    epoch: u64,
}
impl Settle for SettleFresh {
    fn verify_health(&self) -> io::Result<()> {
        Fresh::verify_health(self.custody.as_ref())
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        let c = &self.custody;
        if c.lifecycle.active() != Some(self.epoch)
            || *c.current_sid.lock().unwrap() != self.sid
            || c.current_session.load(Ordering::Acquire) != self.session as usize
            || !c.current_console.load(Ordering::Acquire)
            || Fresh::authentication(c.as_ref()) != self.authentication
        {
            return None;
        }
        // Controller absence, integrity, origin trust, custody and generation.
        Fresh::authorize_owned_stop(c.as_ref())
    }
}
/// Test model of `WindowsGuard::capture`'s epoch pin: a write capture whose
/// request and owned-Stop admissions both refuse once a power or session
/// notice advances the lifecycle epoch it captured at. Its settle renewal is
/// the custody's (`Custody::capture_settle`).
struct PinnedFresh {
    custody: Arc<Custody>,
    epoch: u64,
}
impl PinnedFresh {
    fn new(custody: Arc<Custody>) -> io::Result<Self> {
        let epoch = custody
            .lifecycle
            .active()
            .ok_or(io::ErrorKind::PermissionDenied)?;
        Ok(Self { custody, epoch })
    }
    fn current(&self) -> bool {
        self.custody.lifecycle.active() == Some(self.epoch)
    }
}
impl Fresh for PinnedFresh {
    fn on_battery(&self) -> Option<bool> {
        self.custody.on_battery()
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        Fresh::authentication(self.custody.as_ref())
    }
    fn verify_health(&self) -> io::Result<()> {
        Fresh::verify_health(self.custody.as_ref())
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.current()
            .then(|| Fresh::authorize(self.custody.as_ref()))?
    }
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.current()
            .then(|| Fresh::authorize_owned_stop(self.custody.as_ref()))?
    }
    fn capture_settle(
        &self,
        sid: &str,
        session: u32,
        authentication: Option<(u32, i32)>,
    ) -> io::Result<Arc<dyn Settle>> {
        Fresh::capture_settle(self.custody.as_ref(), sid, session, authentication)
    }
}
impl Custody {
    /// Blocks in the armed owned-Stop or cleanup authorization, as a slow
    /// Windows supplier would (`owned_stop_stall`). A job on paused time
    /// (`Fixture::worker`) measures its own progress on that clock, so the
    /// stall moves it by exactly its length; on a running clock it takes that
    /// long in real time.
    fn stall_owned_stop(&self) {
        let stall = {
            let mut armed = self.owned_stop_stall.lock().unwrap();
            match armed.as_mut() {
                Some((1, by)) => {
                    let by = *by;
                    *armed = None;
                    Some(by)
                }
                Some((nth, _)) => {
                    *nth -= 1;
                    None
                }
                None => None,
            }
        };
        match stall {
            Some(by) if clock_is_paused() => advance_paused_clock(by),
            Some(by) => std::thread::sleep(by),
            None => {}
        }
    }
}
/// Moves the calling runtime's paused clock forward by `by` from synchronous
/// code, as a call that blocks for that long would. `tokio::time::advance`
/// moves the clock when first polled and then only yields, so one poll is
/// the whole advance.
fn advance_paused_clock(by: Duration) {
    let mut advance = std::pin::pin!(tokio::time::advance(by));
    let mut context = std::task::Context::from_waker(std::task::Waker::noop());
    let _ = advance.as_mut().poll(&mut context);
}
/// Test model of `CleanupGuard`: the Service's cleanup admission, with no
/// console session, SID or logon, at the lifecycle fence its scope pinned.
/// Every authorization runs the native order (`thermal_native::admit_cleanup`)
/// over the model's suppliers: fresh health for origin trust, the pinned
/// fence, a known Windows AC line status, `fresh_refuse` (every refused
/// Windows supplier) and `controller` for power and controllers, and the
/// passed custody's cleanup admission of the scope.
#[cfg(feature = "service-fan-mutation-v2")]
use crate::installed::readiness::thermal_native::{Denial, admit_cleanup};
#[cfg(feature = "service-fan-mutation-v2")]
struct CleanupFresh {
    custody: Arc<Custody>,
    passed: Arc<dyn ReadCustody>,
    scope: CleanupScope,
    epoch: u64,
}
#[cfg(feature = "service-fan-mutation-v2")]
impl CleanupFresh {
    fn capture(
        custody: Arc<Custody>,
        passed: Arc<dyn ReadCustody>,
        scope: CleanupScope,
    ) -> io::Result<Arc<dyn Cleanup>> {
        Fresh::verify_health(custody.as_ref())?;
        let epoch = custody
            .lifecycle
            .fence_for(scope.write_scope())
            .ok_or(io::ErrorKind::PermissionDenied)?;
        let fresh = Self {
            custody,
            passed,
            scope,
            epoch,
        };
        // As `CleanupGuard::capture`: a refusal whose cause is fatal is
        // reported as fatal by the health check after it.
        let proof = fresh.admit();
        Fresh::verify_health(fresh.custody.as_ref())?;
        proof.map_err(Denial::error)?;
        Ok(Arc::new(fresh))
    }
    fn admit(&self) -> Result<ReadAdmission, Denial> {
        let c = &self.custody;
        c.stall_owned_stop();
        admit_cleanup(
            || Fresh::verify_health(c.as_ref()).is_ok(),
            || Some(true),
            || c.lifecycle.fence_for(self.scope.write_scope()) == Some(self.epoch),
            || {
                if c.fresh_refuse.load(Ordering::Acquire) || c.on_battery().is_none() {
                    Err(Denial::Admission)
                } else if c.controller.load(Ordering::Acquire) {
                    Err(Denial::CompetingController)
                } else {
                    Ok(())
                }
            },
            || self.passed.authorize_cleanup(self.scope),
        )
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
impl Cleanup for CleanupFresh {
    fn scope(&self) -> CleanupScope {
        self.scope
    }
    fn on_battery(&self) -> Option<bool> {
        self.custody.on_battery()
    }
    fn verify_health(&self) -> io::Result<()> {
        Fresh::verify_health(self.custody.as_ref())
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        self.admit().ok()
    }
}
struct Origin(Arc<crate::loaded_origin::lifetime::Lifetime>, Arc<Custody>);
impl StatusOrigin for Origin {
    fn power_epoch(&self) -> Option<u64> {
        self.1.lifecycle.active()
    }
    fn admit_owned_stop(&self) -> io::Result<()> {
        self.0.verify_owned_stop()
    }
    fn verify(&self) -> io::Result<()> {
        self.0.verify()
    }
    fn revoke(&self) {
        self.0.stop();
    }
    fn confirm_metadata_drain(&self) -> io::Result<()> {
        Ok(())
    }
    fn confirm_transport_drain(&self) -> io::Result<()> {
        Ok(())
    }
}
/// Parks the fixture worker at mutation retirement until the test opens the
/// gate, and tells the test when the worker gets there. Both sides wait on an
/// event rather than spin, so a loaded host only delays them. Dropping the gate
/// opens it, so a failed test never leaves the worker parked.
struct RetirementGate(Arc<GateState>);
#[derive(Default)]
struct GateState {
    reached: tokio::sync::Notify,
    open: StdMutex<bool>,
    opened: std::sync::Condvar,
}
impl RetirementGate {
    /// A bound that only catches a hang. Each recipe in these tests runs on
    /// paused fixture time, so a healthy one never comes near it.
    const LIVENESS: Duration = Duration::from_secs(30);
    fn install(rig: &Rig) -> Self {
        let state = Arc::new(GateState::default());
        let hook = state.clone();
        rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
            hook.reached.notify_one();
            let mut open = hook.open.lock().unwrap();
            while !*open {
                open = hook.opened.wait(open).unwrap();
            }
        }));
        Self(state)
    }
    async fn reached(&self) {
        tokio::time::timeout(Self::LIVENESS, self.0.reached.notified())
            .await
            .expect("the worker reaches mutation retirement");
    }
    fn open(&self) {
        *self.0.open.lock().unwrap() = true;
        self.0.opened.notify_all();
    }
}
impl Drop for RetirementGate {
    fn drop(&mut self) {
        self.open();
    }
}
/// Bounds only waits that explicit sequencing already orders, so it turns a
/// hang into a failure and never decides the race. A wait whose bound is the
/// property under test keeps its own.
const HANG_GUARD: Duration = Duration::from_secs(120);
/// What a held exchange leaves of the test clock (`Rig::held`).
#[derive(Clone, Copy, PartialEq, Eq)]
enum HeldClock {
    /// Brought level with the real clock, so later work that ages results on
    /// it or takes a deadline from it sees the time that passed.
    CatchUp,
    /// Left where the exchange held it. The exchange returns a result judged on
    /// the test clock, such as a read's freshness, whose deadline the held
    /// clock measured; the time the exchange lost stays lost.
    Keep,
}
/// Owned HyperBoost's injected cooling-pad observation and managed journal
/// (`hyperboost::Probes`): the pad is absent and the journal is absent until a
/// test changes them. `script` answers the next observations in order.
#[cfg(feature = "service-hyperboost-v1")]
#[derive(Clone)]
struct HyperBoostProbes {
    pad: Arc<std::sync::Mutex<Pad>>,
    script: Arc<std::sync::Mutex<std::collections::VecDeque<Pad>>>,
    observations: Arc<AtomicUsize>,
    /// Holds the status observer's pad scan: `STALL_UNTIL_CANCELLED` until
    /// the observer cancels it, as the native scan stops, or
    /// `STALL_THROUGH_CANCEL` for an OS call that does not return.
    status_stall: Arc<AtomicU8>,
    /// The status observer's pad scans and journal reads.
    status_reads: Arc<AtomicUsize>,
    /// Holds the request gates' pad scans from this one on (the first is 1)
    /// until their job cancels them, as the native scan stops between its
    /// enumeration calls. 0 holds none.
    gate_stall_from: Arc<AtomicUsize>,
    /// The gate scans that ended at their job's cancellation.
    gate_cancellations: Arc<AtomicUsize>,
    /// `None`: a read that fails.
    journal: Arc<std::sync::Mutex<Option<Option<Vec<u8>>>>>,
    journal_reads: Arc<AtomicUsize>,
}
#[cfg(feature = "service-hyperboost-v1")]
impl HyperBoostProbes {
    const STALL_UNTIL_CANCELLED: u8 = 1;
    // Only the Stop tests hold a scan through its cancellation, and they
    // need the fan runtime.
    #[cfg_attr(not(feature = "service-fan-mutation-v2"), allow(dead_code))]
    const STALL_THROUGH_CANCEL: u8 = 2;
    fn new() -> Self {
        Self {
            pad: Arc::new(std::sync::Mutex::new(Pad::Absent)),
            script: Arc::default(),
            observations: Arc::default(),
            status_stall: Arc::default(),
            status_reads: Arc::default(),
            gate_stall_from: Arc::default(),
            gate_cancellations: Arc::default(),
            journal: Arc::new(std::sync::Mutex::new(Some(None))),
            journal_reads: Arc::default(),
        }
    }
    /// The same pad and journal as the metadata observer reads them for
    /// the status: not counted and not scripted, so a status never takes an
    /// observation a request's gate was scripted to make.
    fn status_probes(&self) -> Probes {
        let (pad, journal) = (self.pad.clone(), self.journal.clone());
        let (stall, scans, reads) = (
            self.status_stall.clone(),
            self.status_reads.clone(),
            self.status_reads.clone(),
        );
        Probes::new(
            move |cancelled| {
                scans.fetch_add(1, Ordering::AcqRel);
                loop {
                    match stall.load(Ordering::Acquire) {
                        Self::STALL_UNTIL_CANCELLED if cancelled.load(Ordering::Acquire) => {
                            return Pad::ObservationFailed;
                        }
                        0 => return *pad.lock().unwrap(),
                        _ => std::thread::sleep(Duration::from_millis(1)),
                    }
                }
            },
            Arc::new(move || {
                reads.fetch_add(1, Ordering::AcqRel);
                journal
                    .lock()
                    .unwrap()
                    .clone()
                    .ok_or_else(|| io::Error::other("injected journal read failure"))
            }),
        )
    }
    fn probes(&self) -> Probes {
        let (pad, script, observations) = (
            self.pad.clone(),
            self.script.clone(),
            self.observations.clone(),
        );
        let (journal, reads) = (self.journal.clone(), self.journal_reads.clone());
        let (stall, cancellations) = (
            self.gate_stall_from.clone(),
            self.gate_cancellations.clone(),
        );
        Probes::new(
            move |cancelled| {
                let scan = observations.fetch_add(1, Ordering::AcqRel) + 1;
                let held =
                    || matches!(stall.load(Ordering::Acquire), from if from != 0 && scan >= from);
                // The cap ends the job thread of a test that failed before
                // it released the scan.
                let began = std::time::Instant::now();
                loop {
                    // The native scan checks its cancellation first and
                    // between its calls, and a cancelled scan failed.
                    if cancelled.load(Ordering::Acquire) {
                        cancellations.fetch_add(1, Ordering::AcqRel);
                        return Pad::ObservationFailed;
                    }
                    if !held() || began.elapsed() > Duration::from_secs(8) {
                        break;
                    }
                    std::thread::sleep(Duration::from_millis(1));
                }
                let next = script.lock().unwrap().pop_front();
                next.unwrap_or(*pad.lock().unwrap())
            },
            Arc::new(move || {
                reads.fetch_add(1, Ordering::AcqRel);
                journal
                    .lock()
                    .unwrap()
                    .clone()
                    .ok_or_else(|| io::Error::other("injected journal read failure"))
            }),
        )
    }
}
struct Rig {
    #[cfg(feature = "service-brightness-hid-usage-v1")]
    brightness: crate::installed::readiness::thermal_mutation::brightness_hid::fake::Shared,
    // Status observations started, and a factory that refuses every capture
    // as native's refuses a SID it cannot root or a store it cannot open.
    #[cfg(feature = "service-brightness-hid-usage-v1")]
    observations: Arc<AtomicUsize>,
    #[cfg(feature = "service-brightness-hid-usage-v1")]
    store_unavailable: Arc<AtomicBool>,
    #[cfg(feature = "service-hyperboost-v1")]
    hyperboost: HyperBoostProbes,
    #[cfg(feature = "service-fan-mutation-v2")]
    runtime: Runtime,
    #[cfg(feature = "service-fan-mutation-v2")]
    factory_calls: Arc<AtomicUsize>,
    #[cfg(feature = "service-fan-mutation-v2")]
    storage: Arc<Storage>,
    source: Rc<Mutex<Box<dyn StatusSource>>>,
    port: Fixture,
    files: MemoryFiles,
    custody: Arc<Custody>,
    metadata: Arc<AtomicU8>,
    entered: Rc<std::cell::Cell<u64>>,
    reader: ReadSource,
    origin: Arc<crate::loaded_origin::lifetime::Lifetime>,
    fatal_capture: Arc<AtomicBool>,
    #[cfg(feature = "service-battery-policy-v1")]
    battery_clock: Rc<dyn Fn() -> Option<Instant>>,
}
/// Every setter applies intent saved before it: an explicit request's
/// override, or, for a reconciliation step's journal id (`is_step_request`),
/// the saved profiles it applies.
fn saved_intent(files: &MemoryFiles, entry: &Value) -> bool {
    #[cfg(feature = "service-profile-triggers-v1")]
    let reconciled = entry["request"]
        .as_str()
        .is_some_and(crate::installed::readiness::thermal_mutation::is_step_request);
    #[cfg(not(feature = "service-profile-triggers-v1"))]
    let reconciled = {
        let _ = entry;
        false
    };
    // An owned HyperBoost request (`hyperboost_owned::Rig::hyperboost`'s
    // request ids) stores nothing: its rows write saved profile intent, or
    // managed's Balanced restore (destination 1) for an exit that skips it.
    #[cfg(feature = "service-hyperboost-v1")]
    if entry["request"]
        .as_str()
        .is_some_and(|r| r.starts_with("aaaaaaaa-bbbb-cccc-eeee-"))
    {
        return entry["destination"] == 1 || files.read("profiles.json").unwrap().is_some();
    }
    let saved = if reconciled {
        "profiles.json"
    } else {
        "manual-overrides.json"
    };
    files.read(saved).unwrap().is_some()
}
impl Rig {
    fn new(files: MemoryFiles) -> Self {
        Self::start(files, None)
    }
    /// A rig over the files an earlier rig left, whose custody reads their
    /// journal as production's `ReadLease` does: a restarted Service, with a
    /// fresh transaction pool on a fresh port.
    fn restarted(files: MemoryFiles) -> Self {
        Self::start(files, Some(Vec::new()))
    }
    /// `restarted`, whose custody first fails each journal read with one of
    /// `failures`.
    fn restarted_failing(files: MemoryFiles, failures: Vec<io::Error>) -> Self {
        Self::start(files, Some(failures))
    }
    fn start(files: MemoryFiles, journal: Option<Vec<io::Error>>) -> Self {
        let port = Fixture::new();
        let custody = Custody::new(&port);
        // A restarted Service is a new generation with its own owner nonce.
        #[cfg(feature = "service-keyboard-driver-mode-v1")]
        custody
            .driver_mode
            .bind(&files, if journal.is_some() { 'b' } else { 'a' });
        if let Some(failures) = journal {
            *custody.journal.lock().unwrap() = Some(files.clone());
            *custody.journal_failures.lock().unwrap() = failures.into();
        }
        let persisted = files.clone();
        port.state.lock().unwrap().before_write = Some(Arc::new(move |_number| {
            // Battery overrides are memory-scoped, not manual-overrides fields.
            let journal = persisted.read("thermal-mutation.journal").unwrap().unwrap();
            let mut rows = journal.split(|b| *b == b'\n').filter(|s| !s.is_empty());
            let last = rows.next_back().unwrap();
            let entry: Value = serde_json::from_slice(last).unwrap();
            if entry.get("tfc").is_some() {
                assert_eq!(entry["progress"], 70);
            } else if entry.get("battery").is_some() {
                assert_eq!(entry["progress"], 60);
            } else if entry.get("fan").is_some() {
                assert!(saved_intent(&persisted, &entry), "{entry}");
                assert!(matches!(
                    entry["progress"].as_u64(),
                    Some(10 | 12 | 14 | 16 | 40 | 42)
                ));
            } else {
                assert!(saved_intent(&persisted, &entry), "{entry}");
                // Each setter is preceded by its own intent, which directly
                // follows the prior step of one of the destination's chains
                // (a battery preset's HyperBoost restore chain included).
                let prior: Value = serde_json::from_slice(rows.next_back().unwrap()).unwrap();
                assert_eq!(prior["request"], entry["request"]);
                let follows = openblade_domain::thermal_mutation::chains(
                    entry["destination"].as_i64().unwrap() as i32,
                )
                .any(|chain| {
                    chain
                        .iter()
                        .position(|step| Some(*step as u64) == entry["progress"].as_u64())
                        .is_some_and(|at| {
                            at % 2 == 1
                                && at + 1 < chain.len()
                                && prior["progress"] == chain[at - 1] as u8
                        })
                });
                assert!(follows, "{entry} after {prior}");
            }
            assert_eq!(entry["user_sid"], SID);
        }));
        let read = port.clone();
        let write = port.clone();
        let pool = port.clone();
        let reader = ReadSource::injected_mutation(
            custody.clone(),
            MutationFactories {
                read: Box::new(move |custody| read.read(Arc::new(move || custody.authorize()))),
                write: Box::new(move |permit| write.write(permit)),
                quarantine: Box::new(move |ids| pool.quarantine(ids)),
            },
        );
        // Storage reaches the journal through custody, as `CustodialFiles` does.
        let custodial = CustodialMemory::new(files.clone(), custody.clone());
        let storage = Storage::open(Box::new(custodial.clone()), SID).unwrap();
        let other_files = custodial;
        #[cfg(feature = "service-fan-mutation-v2")]
        let retained_storage = storage.clone();
        let fresh = custody.clone();
        let fatal_capture = Arc::new(AtomicBool::new(false));
        let capture = fatal_capture.clone();
        let mailbox = MailboxSource::Reader(reader.clone());
        let factory_calls = Arc::new(AtomicUsize::new(0));
        let calls = factory_calls.clone();
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let store_unavailable = Arc::new(AtomicBool::new(false));
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let unrooted = store_unavailable.clone();
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let (guard_custody, guard_capture) = (fresh.clone(), capture.clone());
        let runtime = Runtime::injected(&mailbox, move |sid, session, _, admission| {
            calls.fetch_add(1, Ordering::AcqRel);
            #[cfg(feature = "service-brightness-hid-usage-v1")]
            if unrooted.load(Ordering::Acquire) {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            if capture.load(Ordering::Acquire) {
                return Err(
                    crate::installed::readiness::thermal_mutation::fatal_admission(
                        "injected lifecycle capture lock poisoned",
                    ),
                );
            }
            if sid != SID || session as usize != fresh.current_session.load(Ordering::Acquire) {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            // Production's storage capture pins no epoch; its model is the
            // same in either mode, as only a refused write capture takes it.
            #[cfg(feature = "service-profile-triggers-v1")]
            if admission == crate::installed::readiness::thermal_mutation::Capture::Storage {
                let proof = tfc_corrections::CapturedStorage::new(fresh.clone(), session)?;
                return Ok((storage.clone(), Arc::new(proof)));
            }
            let _ = admission;
            if fresh.controller.load(Ordering::Acquire) {
                return Err(crate::installed::readiness::thermal_mutation::competing_controller());
            }
            let proof: Arc<dyn Fresh> = if fresh.pinned_fresh.load(Ordering::Acquire) {
                Arc::new(PinnedFresh::new(fresh.clone())?)
            } else if fresh.scoped_fresh.load(Ordering::Acquire) {
                if !fresh.current_console.load(Ordering::Acquire) {
                    return Err(io::ErrorKind::PermissionDenied.into());
                }
                Arc::new(tfc_corrections::CapturedFresh::new(fresh.clone(), session)?)
            } else {
                fresh.clone()
            };
            if fresh.other_storage.load(Ordering::Acquire) {
                return Ok((Storage::open(Box::new(other_files.clone()), SID)?, proof));
            }
            Ok((storage.clone(), proof))
        });
        #[cfg(feature = "service-battery-policy-v1")]
        let battery_clock = runtime.battery_clock_for_tests();
        let entered = runtime.entered.clone();
        #[cfg(feature = "service-hyperboost-v1")]
        let hyperboost = HyperBoostProbes::new();
        #[cfg(feature = "service-hyperboost-v1")]
        runtime.read_hyperboost(hyperboost.probes());
        let metadata = Arc::new(AtomicU8::new(0));
        let origin = Arc::new(crate::loaded_origin::lifetime::Lifetime::default());
        #[cfg(feature = "service-hyperboost-v1")]
        let observed_hyperboost = hyperboost.status_probes();
        #[cfg(not(feature = "service-hyperboost-v1"))]
        let observed_hyperboost = ();
        // A scripted display-brightness lane: no Windows call is reached.
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let brightness = {
            let (lane, script) =
                crate::installed::readiness::thermal_mutation::brightness_hid::fake::lane();
            runtime.compose_brightness(lane);
            script
        };
        // The display-brightness press admission: the factory's device checks
        // without its store, as native composes it.
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        runtime.compose_brightness_guard(Box::new(move |sid, session, _| {
            if guard_capture.load(Ordering::Acquire) {
                return Err(
                    crate::installed::readiness::thermal_mutation::fatal_admission(
                        "injected lifecycle capture lock poisoned",
                    ),
                );
            }
            if sid != SID
                || session as usize != guard_custody.current_session.load(Ordering::Acquire)
            {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            if guard_custody.controller.load(Ordering::Acquire) {
                return Err(crate::installed::readiness::thermal_mutation::competing_controller());
            }
            Ok(guard_custody.clone() as Arc<dyn Fresh>)
        }));
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let observations = Arc::new(AtomicUsize::new(0));
        #[cfg(feature = "service-brightness-hid-usage-v1")]
        let observed = observations.clone();
        let source = injected_mutation_source(
            Arc::new(Origin(origin.clone(), custody.clone())),
            mailbox,
            runtime.clone(),
            metadata.clone(),
            {
                let port = port.clone();
                Arc::new(move || {
                    #[cfg(feature = "service-brightness-hid-usage-v1")]
                    observed.fetch_add(1, Ordering::AcqRel);
                    port.count_thread();
                })
            },
            observed_hyperboost,
        );
        Self {
            #[cfg(feature = "service-hyperboost-v1")]
            hyperboost,
            #[cfg(feature = "service-brightness-hid-usage-v1")]
            brightness,
            #[cfg(feature = "service-brightness-hid-usage-v1")]
            observations,
            #[cfg(feature = "service-brightness-hid-usage-v1")]
            store_unavailable,
            #[cfg(feature = "service-fan-mutation-v2")]
            runtime,
            #[cfg(feature = "service-fan-mutation-v2")]
            factory_calls,
            #[cfg(feature = "service-fan-mutation-v2")]
            storage: retained_storage,
            source: Rc::new(Mutex::new(source)),
            port,
            files,
            custody,
            metadata,
            entered,
            reader,
            origin,
            fatal_capture,
            #[cfg(feature = "service-battery-policy-v1")]
            battery_clock,
        }
    }
    async fn request(&self, request: Value) -> Value {
        self.request_as(request, USER, SID).await
    }
    async fn request_as(&self, request: Value, caller: Caller, sid: &str) -> Value {
        let operation = request["operation"].as_str().unwrap().to_owned();
        self.request_payload(
            &operation,
            serde_json::to_vec(&request).unwrap(),
            caller,
            sid,
        )
        .await
    }
    /// A request sent as `payload` exactly, member order included, which a
    /// `Value` sorts.
    async fn request_payload(
        &self,
        operation: &str,
        payload: Vec<u8>,
        caller: Caller,
        sid: &str,
    ) -> Value {
        let bytes = self.request_bytes(operation, payload, caller, sid).await;
        serde_json::from_slice::<Value>(&bytes).unwrap()
    }
    /// The response to `payload` exactly as the encoder wrote it, member order
    /// included.
    async fn request_bytes(
        &self,
        operation: &str,
        payload: Vec<u8>,
        caller: Caller,
        sid: &str,
    ) -> Vec<u8> {
        let (client, server) = duplex(65536);
        let source = self.source.clone();
        let publications = source.lock().await.publications();
        let server = serve_connection(
            Box::new(SidStream {
                inner: test_stream(server, caller, Fault::None),
                sid: sid.to_owned(),
            }),
            source,
            publications,
            Rc::new(Subscriptions::new(2)),
        );
        let client = async move {
            let mut client = client;
            crate::service_frame::write_payload(&mut client, &payload)
                .await
                .unwrap();
            crate::service_frame::read_payload(&mut client).await
        };
        // Boxed: the debug build keeps every nested future on the stack, and
        // the isolated cases run on small thread stacks.
        let (server, bytes) = self
            .held(
                HeldClock::CatchUp,
                Box::pin(async { tokio::join!(server, client) }),
            )
            .await;
        // The server's error explains a missing reply.
        assert!(server.is_ok(), "{operation}: {:?}", server.err());
        let bytes = bytes.unwrap();
        // Preserve the actual encoder bytes. Re-serializing Value sorts object
        // keys and can move the managed polymorphic discriminator out of first place.
        assert!(bytes.starts_with(br#"{"frame":"response","#));
        emit_fixture_frame("peer_response", operation, "response", &bytes);
        println!(
            "NATIVE_THERMAL_EDGE_TRACE {} {}",
            operation,
            std::str::from_utf8(&bytes).unwrap()
        );
        bytes
    }
    /// Asks whether the test has made a job thread wait for something other
    /// than the clock: a held transfer it has not released, or a deliberate
    /// stall of real time. A request in that state can end only at its own
    /// bound on the real clock, which these tests exercise, so a held exchange
    /// does not hold the clock back for it. (A test hook that parks a thread is
    /// seen while it runs: `Fixture::hooked`. A stalled owned-Stop
    /// authorization costs the clock of the runtime it runs on:
    /// `Custody::stall_owned_stop`.)
    fn parks(&self) -> Rc<dyn Fn() -> bool> {
        let state = self.port.state.clone();
        let stalled = self.files.stalled_replace.clone();
        Rc::new(move || {
            let waiting = |release: &Arc<AtomicBool>| !release.load(Ordering::Acquire);
            // A job inside a test hook holds this lock (the fixture calls hooks under
            // it), so it is never waited for: a locked fixture is a parked job.
            let Ok(state) = state.try_lock() else {
                return true;
            };
            #[cfg(feature = "service-keyboard-driver-mode-v1")]
            let real_time =
                state.device_mode_latency.is_some() || state.device_mode_retire_delay.is_some();
            #[cfg(not(feature = "service-keyboard-driver-mode-v1"))]
            let real_time = false;
            #[cfg(feature = "service-lighting-v1")]
            let real_time = real_time
                || state
                    .lighting_held
                    .as_ref()
                    .is_some_and(|(_, release)| waiting(release));
            real_time
                || state.held_read.as_ref().is_some_and(waiting)
                || state
                    .held_write
                    .as_ref()
                    .is_some_and(|(_, release)| waiting(release))
                || stalled.try_lock().is_ok_and(|stall| stall.is_some())
        })
    }
    /// Runs one request exchange on a held test clock, and starts the budget of
    /// every job it runs at the exchange's arrival; a later job that serves no
    /// request keeps its own deadline. The Service bounds an exchange by the
    /// frame deadline or a recipe budget on the test runtime's clock, and its
    /// jobs run on OS threads, so on the real clock a loaded host that leaves a
    /// job thread unscheduled for longer than that bound refuses a request that
    /// was fine. The clock is paused for the exchange and keeps pace with the
    /// real one only while no job thread is busy (`HeldClocks`), then is
    /// brought level with the real clock as `after` says. The bounds themselves
    /// are unchanged: a test that advances the clock past one still expires it.
    /// A clock the test already paused (`frozen`, `tokio::time::pause`) stays
    /// that test's to drive.
    async fn held<T>(&self, after: HeldClock, exchange: impl std::future::Future<Output = T>) -> T {
        let mut exchange_hold = Exchange::enter(self);
        let out = if exchange_hold.holding() {
            HeldClocks::pace(exchange).await
        } else {
            exchange.await
        };
        exchange_hold.end(after).await;
        out
    }
    /// One observation through the source's own entry point, run as a request's
    /// exchange is (see `held`): `budget` is its deadline from now, spent
    /// through the job's own waits and not through the host's delays.
    async fn observe(&self, budget: Duration) -> io::Result<Option<Snapshot>> {
        self.held(
            HeldClock::Keep,
            Box::pin(async {
                let observation = self
                    .source
                    .lock()
                    .await
                    .observe_performance(USER, tokio::time::Instant::now() + budget);
                observation.await
            }),
        )
        .await
    }
    async fn get(&self) -> Value {
        self.request(json!({"operation":"getStatus","version":91,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000099"})).await
    }
    async fn set(&self, n: u8, value: Value) -> Value {
        let response = self.request(json!({"operation":"setPerformanceOverride","version":91,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"value":value})).await;
        assert!(
            response["status"].is_null(),
            "v91 result replies have exactly one body"
        );
        assert!(!response["result"].is_null());
        response
    }
    async fn stop(&self) {
        self.drain().await.unwrap();
        self.source.lock().await.confirm_drained().unwrap();
    }
    /// The source's drain (`StatusSource::drain`) on a test clock held as a
    /// request's exchange is (`held`), so a loaded host that leaves the
    /// cleanup's job threads or the status observer unscheduled does not
    /// spend the drain's deadline, and the first cleanup recipe takes its
    /// budget from the drain's start. A clock the test paused itself is
    /// running again first (`resume_clock_for_stop`). The deadline itself is
    /// unchanged: a job that waits past it on its own clock still expires.
    async fn drain(&self) -> io::Result<()> {
        resume_clock_for_stop();
        self.held(HeldClock::CatchUp, async {
            self.source.lock().await.drain().await
        })
        .await
    }
    /// One origin tick (`StatusSource::process_power_source`), run as a
    /// request's exchange is (`held`): its jobs take their budget from the
    /// tick's start and a loaded host spends none of it.
    #[cfg(feature = "service-temporary-charge-v1")]
    async fn process_power_source(&self) -> io::Result<()> {
        self.held(
            HeldClock::CatchUp,
            Box::pin(async { self.source.lock().await.process_power_source().await }),
        )
        .await
    }
    /// Stops a rig whose case leaves HyperBoost on, in production order:
    /// SCM STOP revokes the origin before the drain. With the lifecycle
    /// triggers native owns that HyperBoost, and these rigs wire no cleanup
    /// admission, so the Stop's own exit is refused: it reports the
    /// HyperBoost it leaves and sends no setter
    /// (`hyperboost_cleanup` covers the admitted exit).
    #[cfg(feature = "service-hyperboost-v1")]
    async fn stop_in_hyperboost(&self) {
        let writes = self.port.state.lock().unwrap().writes;
        self.origin.stop();
        let drained = self.drain().await;
        self.source.lock().await.confirm_drained().unwrap();
        if let Err(error) = drained {
            assert!(
                cfg!(feature = "service-profile-triggers-v1")
                    && error
                        .to_string()
                        .contains("Owned HyperBoost was not exited at Stop"),
                "{error}"
            );
        }
        assert_eq!(self.port.state.lock().unwrap().writes, writes);
    }
}
fn value(preset: i32, lifetime: u8) -> Value {
    json!({"value":preset,"lifetime":lifetime,"customPerformance":null,"gpuClockTuning":null})
}
#[tokio::test]
async fn installed_dispatcher_direct_pair_persists_before_applied_and_retains_publication() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    // Production prepare performs a typed GetStatus readiness probe before
    // serving observations. Use the same injected dispatcher round trip here.
    let metadata = rig.get().await;
    assert_eq!(metadata["success"], true);
    assert_eq!(metadata["status"]["writesBlocked"], true);
    assert!(metadata["status"]["confirmedState"].is_null());
    let initial=rig.request(json!({"operation":"observeState","version":91,"requestId":"dddddddd-bbbb-cccc-dddd-000000000001","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})).await;
    assert_eq!(initial["success"], true);
    assert_eq!(initial["status"]["confirmedState"]["performance"], 5);
    assert_eq!(initial["status"]["confirmedState"]["fans"], 3);
    assert!(initial["status"]["confirmedState"]["customPerformance"].is_null());
    {
        let port = rig.port.state.lock().unwrap();
        assert!(
            port.opened > 0,
            "a real injected read port must precede activation"
        );
        assert_eq!(port.writes, 0);
        assert!(port.sent.iter().any(|request| request.1 == 0x87));
    }
    assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
    assert_eq!(initial["status"]["writesBlocked"], true);
    let result = rig.set(1, value(1, 1)).await;
    let status = rig.get().await;
    assert_eq!(status["status"]["writesBlocked"], true);
    assert_eq!(result["result"]["status"], 1);
    assert_eq!(status["status"]["confirmedState"]["performance"], 1);
    assert_eq!(status["status"]["writesBlocked"], true);
    assert!(status["status"]["capabilities"].is_null());
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["performance"],
        1
    );
    {
        let sent = rig.port.state.lock().unwrap();
        let writes: Vec<_> = sent.sent.iter().filter(|r| r.0 == 13 && r.1 == 2).collect();
        assert_eq!(writes.len(), 2);
        assert_eq!(writes[0].2, vec![1, 1, 0, 0]);
        assert_eq!(writes[1].2, vec![1, 2, 0, 0]);
        assert!(sent.sent.iter().any(|r| r.1 == 0x87));
        assert_eq!(
            sent.sent
                .iter()
                .map(|r| r.3)
                .collect::<std::collections::BTreeSet<_>>()
                .len(),
            sent.sent.len()
        );
    }
    let mut source = rig.source.lock().await;
    let projected = source.snapshot(USER, Instant::now()).await.unwrap();
    let projected = serde_json::to_value(projected).unwrap();
    assert_eq!(projected["performanceOverride"]["value"], 1);
    assert_eq!(projected["desiredState"]["performance"]["value"], 1);
    assert_eq!(projected["confirmedState"]["performance"], 1);
    assert_eq!(projected["writesBlocked"], true);
    drop(source);
    assert_eq!(rig.custody.activations.load(Ordering::Acquire), 1);
    rig.stop().await;
}
#[tokio::test]
async fn installed_temporary_clear_and_application_precedence_use_saved_canonical_profiles() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    assert_eq!(rig.set(1, value(2, 1)).await["result"]["status"], 1);
    assert_eq!(rig.set(2, value(1, 0)).await["result"]["status"], 1);
    assert!(rig.files.document("manual-overrides.json")["overrides"]["performance"].is_null());
    report_game(&rig).await;
    let result = rig.set(3, Value::Null).await;
    let status = rig.get().await;
    assert_eq!(status["status"]["writesBlocked"], true);
    assert_eq!(result["result"]["status"], 1);
    assert_eq!(status["status"]["confirmedState"]["performance"], 2);
    assert_eq!(status["status"]["activePowerSource"], 3);
    // Typed mutation selected the application (confirmed 2 above), but later
    // coarse external metadata cannot claim a resolved application power policy.
    assert_eq!(status["status"]["desiredState"]["performance"]["source"], 1);
    assert!(status["status"]["performanceOverride"].is_null());
    rig.stop().await;
}
#[tokio::test]
async fn installed_ambiguous_submission_fences_restart_and_never_replays() {
    let files = MemoryFiles::default();
    let rig = Rig::new(files.clone());
    rig.port.state.lock().unwrap().fail_write = Some(1);
    let result = rig.set(1, value(1, 1)).await;
    let status = rig.get().await;
    assert_eq!(status["status"]["writesBlocked"], true);
    assert_eq!(result["result"]["status"], 2);
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
    let restarted = Storage::open(Box::new(files), SID).unwrap();
    assert!(restarted.pending());
    assert_eq!(restarted.progress(), Some(1));
    assert_eq!(rig.set(2, value(2, 1)).await["result"]["status"], 2);
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
    rig.stop().await;
}
#[tokio::test]
async fn installed_persistence_failure_and_budget_exhaustion_submit_no_mutation() {
    for exhausted in [false, true] {
        let files = MemoryFiles::default();
        let rig = Rig::new(files);
        if exhausted {
            rig.port.exhaust()
        } else {
            rig.files.fail.store(true, Ordering::Release)
        }
        let result = rig.set(1, value(1, 1)).await;
        let status = rig.get().await;
        assert_eq!(status["status"]["writesBlocked"], true);
        assert_eq!(result["result"]["status"], 2);
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        rig.stop().await;
    }
}
#[tokio::test]
async fn installed_gpu_intent_is_stored_with_explicit_incompleteness_and_no_pair() {
    let rig = Rig::new(MemoryFiles::default());
    let mut desired = value(1, 1);
    desired["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
    let result = rig.set(1, desired).await;
    let status = rig.get().await;
    assert_eq!(status["status"]["writesBlocked"], true);
    // Managed reports a saved but unapplied override as Blocked with this prefix.
    assert_eq!(result["result"]["status"], 2);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    let message = result["result"]["message"].as_str().unwrap();
    assert!(message.starts_with("Performance override intent was stored. Hardware result: "));
    assert!(message.contains("GPU"));
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["gpuClockTuning"]["offsets"]["coreMegahertz"],
        10
    );
    rig.stop().await;
}

#[tokio::test]
async fn installed_source_ineligible_intent_is_rejected_and_power_change_stops_partial_pair() {
    // On battery that Windows confirms, managed
    // `ValidateThermalOverrideCombination` rejects an AC override before
    // storing it (`PerformancePowerSourcePolicy.IsAllowed`), and nothing is
    // written. Managed offers every supported preset on both external
    // classes, so a confirmed limited USB-C adapter applies the saved
    // Performance override with the same commands, after fresh per-command
    // class admission.
    for actual in [0, 1] {
        let files = MemoryFiles::default();
        files.profiles(1);
        let rig = Rig::new(files);
        rig.port.state.lock().unwrap().actual = actual;
        let result = rig.set(1, value(2, 1)).await;
        let status = rig.get().await;
        let message = result["result"]["message"].as_str().unwrap();
        if actual == 0 {
            assert_eq!(result["result"]["status"], 3);
            assert_eq!(
                message,
                "The requested intent was rejected: The performance preset is not valid for this power source."
            );
            assert!(status["status"]["performanceOverride"].is_null());
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            assert!(
                rig.files
                    .read("thermal-mutation.journal")
                    .unwrap()
                    .is_none()
            );
            assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        } else {
            assert_eq!(status["status"]["performanceOverride"]["value"], 2);
            assert_eq!(result["result"]["status"], 1, "{message}");
            assert_eq!(status["status"]["confirmedState"]["performance"], 2);
            assert_eq!(rig.port.state.lock().unwrap().writes, 2);
        }
        rig.stop().await;
    }
    // Between the setters, managed `AdmitOrdinaryThermalCommandAsync` admits
    // any fresh external reading that agrees with Windows AC: full to limited
    // keeps this explicit destination, so the pair completes. Battery
    // disagrees and stops it after the first setter, without replay.
    for (changed, completed) in [(1, true), (0, false)] {
        let rig = Rig::new(MemoryFiles::default());
        rig.port.state.lock().unwrap().change_power = Some((3, changed));
        let result = rig.set(2, value(2, 1)).await;
        let status = rig.get().await;
        let progress = Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .progress();
        if completed {
            assert_eq!(result["result"]["status"], 1, "{result}");
            assert_eq!(status["status"]["confirmedState"]["performance"], 2);
            assert_eq!(rig.port.state.lock().unwrap().writes, 2);
            assert_eq!(progress, Some(5));
        } else {
            assert_eq!(status["status"]["writesBlocked"], true);
            assert_eq!(result["result"]["status"], 2);
            assert_eq!(rig.port.state.lock().unwrap().writes, 1);
            assert_eq!(progress, Some(2));
        }
        rig.stop().await;
    }
}
#[tokio::test]
async fn installed_cleared_performance_override_keeps_the_managed_prefix() {
    let rig = Rig::new(MemoryFiles::default());
    rig.port.state.lock().unwrap().selectors = [2, 2];
    rig.port.state.lock().unwrap().change_power = Some((2, 0));
    let cleared = rig.set(91, Value::Null).await;
    assert_eq!(cleared["result"]["status"], 2);
    assert_eq!(
        cleared["result"]["message"],
        "Performance override was cleared. Hardware result: No supported performance destination is selected by the current power and profile policy."
    );
    rig.stop().await;
}
// Feeds the managed-codec oracle: every operation the composed dispatcher does
// not serve gets managed's unsupported failure and reaches no port or file.
#[tokio::test]
async fn installed_unserved_operations_reply_with_the_managed_unsupported_failure() {
    let rig = Rig::new(MemoryFiles::default());
    let files = rig.files.bytes.lock().unwrap().clone();
    let contract: Value = serde_json::from_str(CONTRACT).unwrap();
    let mut refused = 0;
    for operation in contract["operationFamilies"]
        .as_object()
        .unwrap()
        .values()
        .flat_map(|family| family.as_array().unwrap())
        .filter_map(Value::as_str)
        .filter(|operation| !StatusRequest::serves(operation))
    {
        refused += 1;
        let id = format!("eeeeeeee-ffff-4aaa-8bbb-{refused:012}");
        let response = rig
            .request(json!({"operation": operation, "version": 91, "requestId": id}))
            .await;
        assert_eq!(
            response,
            json!({
                "frame": "response", "version": 91, "requestId": id, "success": false,
                "error": "IPC request operation is unsupported.", "status": null,
                "result": null, "archive": null, "errorCode": null,
            }),
            "{operation}"
        );
    }
    assert!(refused >= 53 - crate::service_status::StatusRequest::OPERATIONS.len());
    {
        let port = rig.port.state.lock().unwrap();
        assert_eq!((port.writes, port.opened, port.power_reads), (0, 0, 0));
    }
    assert_eq!(rig.entered.get(), 0);
    assert_eq!(*rig.files.bytes.lock().unwrap(), files);
    rig.stop().await;
}
#[tokio::test]
async fn installed_canceled_submission_retains_custody_until_actual_callback_retirement() {
    let rig = Rig::new(MemoryFiles::default());
    let release = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().held_write = Some((1, release.clone()));
    {
        let request = rig.set(1, value(1, 1));
        tokio::pin!(request);
        let entered = async {
            loop {
                if rig.port.state.lock().unwrap().writes == 1 {
                    break;
                }
                tokio::task::yield_now().await;
            }
        };
        tokio::select! {_ = &mut request=>panic!("held mutation callback completed"), result = tokio::time::timeout(HANG_GUARD,entered)=>{result.unwrap();}}
    }
    assert!(
        Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .pending()
    );
    {
        let mut source = rig.source.lock().await;
        assert!(
            tokio::time::timeout(std::time::Duration::from_millis(20), source.drain())
                .await
                .is_err()
        );
    }
    assert!(!rig.custody.retired.load(Ordering::Acquire));
    release.store(true, Ordering::Release);
    rig.stop().await;
    assert!(rig.custody.retired.load(Ordering::Acquire));
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
}
#[tokio::test]
async fn installed_final_mismatch_uses_reserved_pair_only_and_preserves_unrelated_manual_fan_intent()
 {
    let files = MemoryFiles::default();
    let fan = json!({"mode":2,"fixedSpeedRpm":3700,"manualCurve":{"cpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"gpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"useHigherTemperatureForBoth":true}});
    files.put("manual-overrides.json",json!({"schemaVersion":8,"overrides":{"performance":null,"fans":2,"fanControl":fan,"lighting":null,"customPerformance":null,"gpuClockTuning":null}}));
    let rig = Rig::new(files);
    rig.port.state.lock().unwrap().mismatch = true;
    rig.port.state.lock().unwrap().selectors = [2, 2];
    rig.port.state.lock().unwrap().fans = [0, 0]; // automatic baseline: no0581 coupled fan restore
    let result = rig.set(1, value(1, 1)).await;
    let status = rig.get().await;
    assert_eq!(status["status"]["writesBlocked"], true);
    assert_eq!(result["result"]["status"], 2);
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["fanControl"],
        fan
    );
    {
        let state = rig.port.state.lock().unwrap();
        let after_last_write = state.sent.iter().rposition(|r| r.1 == 2).unwrap() + 1;
        assert_eq!(
            state.sent[after_last_write..]
                .iter()
                .filter(|r| r.1 == 0x82)
                .count(),
            2
        );
        assert!(!state.sent[after_last_write..].iter().any(|r| r.1 == 0x87));
    }
    rig.stop().await;
}
#[test]
fn installed_storage_preserves_future_legacy_corrupt_and_uncommitted_documents() {
    for bytes in [
        br#"{"schemaVersion":9,"overrides":{}}"#.as_slice(),
        br#"{"schemaVersion":11,"overrides":{"performance":null,"fans":null,"lighting":null}}"#
            .as_slice(),
        br#"{"schemaVersion":7,"overrides":{"performance":null,"fans":null,"lighting":null}}"#
            .as_slice(),
        br#"{"schemaVersion":8,"schemaVersion":8,"overrides":{}}"#.as_slice(),
        br#"{"schemaVersion":8,"overrides":{"performance":99,"fans":null,"lighting":null}}"#
            .as_slice(),
    ] {
        let files = MemoryFiles::default();
        files
            .bytes
            .lock()
            .unwrap()
            .insert("manual-overrides.json".into(), bytes.to_vec());
        assert!(Storage::open(Box::new(files.clone()), SID).is_err());
        assert_eq!(files.read("manual-overrides.json").unwrap().unwrap(), bytes);
    }
    let files = MemoryFiles::default();
    files.profiles(1);
    let before = files.read("profiles.json").unwrap().unwrap();
    files.put(
        "battery-configuration.commit.json",
        json!({"schemaVersion":crate::configuration::CURRENT_SCHEMA,"profileSha256":"wrong","preferencesSha256":"wrong"}),
    );
    assert!(Storage::open(Box::new(files.clone()), SID).is_err());
    assert_eq!(files.read("profiles.json").unwrap().unwrap(), before);
    assert!(files.read("manual-overrides.json").unwrap().is_none());
}

// Managed PersistentOverrideStore reads schemas 8 to 10 and always writes 10.
// It rejects a non-null zone control below 10 and a non-false keyboard-effect
// member below 9, case-insensitively at any depth, before typed migration.
#[test]
fn installed_storage_reads_older_override_schemas_and_writes_the_current_one() {
    let document = |schema: i64, lighting: Value| json!({"schemaVersion":schema,"overrides":{"performance":null,"fans":null,"lighting":lighting,"customPerformance":null,"fanControl":null,"gpuClockTuning":null}});
    for (schema, member, value) in [
        (9, "vaporChamberLightingEnabled", json!(true)),
        (8, "TouchpadStatusIndicatorEnabled", json!(false)),
        (8, "controlsKeyboardEffect", Value::Null),
    ] {
        let mut lighting = serde_json::Map::new();
        lighting.insert(member.into(), value);
        let files = MemoryFiles::default();
        files.put(
            "manual-overrides.json",
            document(schema, Value::Object(lighting)),
        );
        let before = files.read("manual-overrides.json").unwrap();
        assert!(
            Storage::open(Box::new(files.clone()), SID).is_err(),
            "{member}"
        );
        assert_eq!(files.read("manual-overrides.json").unwrap(), before);
    }
    for schema in [8, 9, 10] {
        let files = MemoryFiles::default();
        files.profiles(1);
        files.put("manual-overrides.json", document(schema, Value::Null));
        let storage = Storage::open(Box::new(files.clone()), SID).unwrap();
        storage
            .save(
                Some(super::super::thermal_storage::Override {
                    value: 2,
                    lifetime: 1,
                    custom_performance: None,
                    gpu_clock_tuning: None,
                }),
                0,
            )
            .unwrap();
        let saved = files.document("manual-overrides.json");
        assert_eq!(saved["schemaVersion"], 10, "{schema}");
        assert_eq!(saved["overrides"]["performance"], 2);
    }
    // Stored intent admits managed MaximumSupportedSpeedRpm; the native fan
    // write admission stays at 5400, so the saved target is refused at use.
    let files = MemoryFiles::default();
    files.profiles(1);
    let mut fan = super::super::thermal_storage::legacy_fan(1);
    fan["fixedSpeedRpm"] = json!(5500);
    let mut saved = document(10, Value::Null);
    saved["overrides"]["fans"] = json!(1);
    saved["overrides"]["fanControl"] = fan;
    files.put("manual-overrides.json", saved);
    let storage = Storage::open(Box::new(files.clone()), SID).unwrap();
    assert!(saved_fan(&storage, 0).is_err());
    let mut fan = super::super::thermal_storage::legacy_fan(1);
    fan["fixedSpeedRpm"] = json!(5600);
    let mut beyond = document(10, Value::Null);
    beyond["overrides"]["fans"] = json!(1);
    beyond["overrides"]["fanControl"] = fan;
    files.put("manual-overrides.json", beyond);
    assert!(Storage::open(Box::new(files), SID).is_err());
}

#[test]
fn installed_override_lifetimes_replace_each_other_and_temporary_expires_on_power_change() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let storage = Storage::open(Box::new(files.clone()), SID).unwrap();
    let override_value = |value, lifetime| super::super::thermal_storage::Override {
        value,
        lifetime,
        custom_performance: None,
        gpu_clock_tuning: None,
    };
    storage.save(Some(override_value(2, 0)), 0).unwrap();
    assert_eq!(saved_desired(&storage, 0).unwrap().unwrap().0.wire(), 2);
    storage.save(Some(override_value(1, 1)), 0).unwrap();
    assert_eq!(saved_desired(&storage, 0).unwrap().unwrap().1, 4);
    storage.save(None, 0).unwrap();
    assert_eq!(saved_desired(&storage, 0).unwrap().unwrap().1, 1);
    storage.save(Some(override_value(2, 0)), 0).unwrap();
    assert_eq!(saved_desired(&storage, 2).unwrap().unwrap().0.wire(), 1);
    assert!(files.document("manual-overrides.json")["overrides"]["performance"].is_null());
}

// Explicit private identity wrapper; delegates only the in-memory Duplex stream.
struct SidStream {
    inner: Box<dyn Stream>,
    sid: String,
}
impl AsyncRead for SidStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        b: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_read(cx, b)
    }
}
impl AsyncWrite for SidStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        b: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(&mut *self.inner).poll_write(cx, b)
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_flush(cx)
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_shutdown(cx)
    }
}
impl Stream for SidStream {
    fn server_pid(&self) -> io::Result<u32> {
        self.inner.server_pid()
    }
    fn caller_after_read(&self) -> io::Result<Caller> {
        self.inner.caller_after_read()
    }
    fn control_session(&self) -> io::Result<Option<u32>> {
        self.inner.control_session()
    }
    fn user_sid_after_read(&self) -> io::Result<String> {
        self.inner.caller_after_read()?;
        Ok(self.sid.clone())
    }
}
#[tokio::test]
async fn installed_private_projection_is_bound_to_sid_for_get_observe_and_subscription() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    assert_eq!(rig.set(1, value(2, 1)).await["result"]["status"], 1);
    for operation in ["getStatus", "observeState", "subscribeStatus"] {
        let request = json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000077","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0});
        // Get/subscription DTOs reject observation-only members.
        let request = if operation == "observeState" {
            request
        } else {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000077"})
        };
        let own = rig.request_as(request.clone(), USER, SID).await;
        assert_eq!(
            own["status"]["profiles"]["applicationProfiles"][0]["executablePath"],
            r"C:\Games\Game.exe"
        );
        for (caller, sid) in [
            (USER, "S-1-5-21-111-222-333-1002"),
            (
                Caller {
                    logon: Logon::System,
                    ..USER
                },
                SID,
            ),
            (
                Caller {
                    session: 8,
                    control_session: Some(7),
                    ..USER
                },
                SID,
            ),
        ] {
            let other = rig.request_as(request.clone(), caller, sid).await;
            let encoded = other.to_string();
            assert!(!encoded.contains("Game.exe"));
            assert!(other["status"]["performanceOverride"].is_null());
            if !other["status"].is_null() {
                assert_eq!(other["status"]["writesBlocked"], true);
            }
        }
    }
    rig.stop().await;
}

#[tokio::test]
async fn installed_later_submission_readback_and_custody_failures_never_publish_applied_or_replay()
{
    // Every cut uses actual dispatcher -> Runtime -> Store adapter -> Reader/worker.
    // The only substitutions are explicit in-memory authority/files/native I/O.
    for (cut, progress, writes) in [(0, 3, 2), (1, 4, 2), (2, 4, 2), (3, 2, 1), (4, 5, 2)] {
        let rig = Rig::new(MemoryFiles::default());
        {
            let mut port = rig.port.state.lock().unwrap();
            match cut {
                0 => port.fail_write = Some(2),
                1 => port.final_read_timeout = true,
                2 => port.final_read_malformed = true,
                3 => {
                    let custody = Arc::downgrade(&rig.custody);
                    port.on_power = Some(Arc::new(move |number| {
                        if number == 3 {
                            custody
                                .upgrade()
                                .unwrap()
                                .live
                                .store(false, Ordering::Release);
                        }
                    }));
                }
                4 => {
                    let custody = Arc::downgrade(&rig.custody);
                    port.on_mutation_retired = Some(Arc::new(move || {
                        custody
                            .upgrade()
                            .unwrap()
                            .live
                            .store(false, Ordering::Release);
                    }));
                }
                _ => unreachable!(),
            }
        }
        let result = rig.set(1, value(1, 1)).await;
        assert_eq!(result["result"]["status"], 2, "cut {cut}");
        assert!(result["status"].is_null());
        let status = rig.get().await;
        assert_eq!(status["status"]["writesBlocked"], true);
        assert_eq!(status["status"]["writesBlocked"], true);
        let journal = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
        let entries: Vec<Value> = journal
            .split(|b| *b == b'\n')
            .filter(|line| !line.is_empty())
            .map(|line| serde_json::from_slice(line).unwrap())
            .collect();
        assert_eq!(entries.len(), progress + 1);
        let restarted = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
        assert_eq!(restarted.progress(), Some(progress as u8));
        assert_eq!(restarted.pending(), progress < 5);
        for (index, entry) in entries.iter().enumerate() {
            assert_eq!(entry["progress"], index);
            assert_eq!(entry["request"], "aaaaaaaa-bbbb-cccc-dddd-000000000001");
            assert_eq!(entry["user_sid"], SID);
            assert_eq!(entry["requested"]["value"], 1);
        }
        if cut < 3 {
            assert_eq!(rig.set(2, value(2, 1)).await["result"]["status"], 2);
            assert_eq!(
                rig.files.read("thermal-mutation.journal").unwrap().unwrap(),
                journal
            );
        } else {
            // Authority loss cannot publish even the independently confirmed
            // late result as current private state. Reading does not replay it.
            assert!(status["status"]["performanceOverride"].is_null());
            assert_eq!(rig.get().await["status"]["writesBlocked"], true);
        }
        {
            let port = rig.port.state.lock().unwrap();
            assert_eq!(port.writes, writes);
            assert_eq!(
                port.opened, port.retired,
                "actual native callback retirement"
            );
        }
        assert!(!rig.custody.retired.load(Ordering::Acquire));
        rig.stop().await;
        assert!(rig.custody.retired.load(Ordering::Acquire));
        assert_eq!(rig.port.state.lock().unwrap().writes, writes);
    }
}

fn manual_files() -> MemoryFiles {
    let files = MemoryFiles::default();
    let fan = json!({"mode":2,"fixedSpeedRpm":3700,"manualCurve":{"cpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"gpu":[{"temperatureCelsius":30,"speedRpm":2000},{"temperatureCelsius":90,"speedRpm":5400}],"useHigherTemperatureForBoth":true}});
    files.put("manual-overrides.json",json!({"schemaVersion":8,"overrides":{"performance":null,"fans":2,"fanControl":fan,"lighting":null,"customPerformance":null,"gpuClockTuning":null}}));
    files
}
#[test]
fn policy_inputs_change_with_every_input_saved_policy_reads() {
    // FanPermit skips re-resolving saved policy only while this digest is
    // unchanged, so each input resolution reads must move it.
    let files = MemoryFiles::default();
    files.profiles(1);
    let storage = Storage::open(Box::new(files.clone()), SID).unwrap();
    let mut seen = vec![storage.policy_inputs().unwrap()];
    assert_eq!(storage.policy_inputs().unwrap(), seen[0]);
    let changed = |seen: &mut Vec<[u8; 32]>| {
        let next = storage.policy_inputs().unwrap();
        assert!(!seen.contains(&next));
        seen.push(next);
    };
    files.profiles(2);
    changed(&mut seen);
    // A temporary override, then a persistent one through this storage.
    storage
        .save(Some(serde_json::from_value(value(2, 0)).unwrap()), 0)
        .unwrap();
    changed(&mut seen);
    storage
        .save(Some(serde_json::from_value(value(2, 1)).unwrap()), 0)
        .unwrap();
    changed(&mut seen);
    // Bytes this storage did not write.
    let mut foreign = files.document("manual-overrides.json");
    foreign["overrides"]["performance"] = json!(1);
    files.put("manual-overrides.json", foreign);
    changed(&mut seen);
    files.put(crate::configuration::RECOVERY_REQUIRED, json!({}));
    changed(&mut seen);
}
#[test]
fn policy_inputs_change_with_a_temporary_alone() {
    // Temporaries live only in this storage's memory, so no file byte moves
    // when one is replaced, added or expires; the digest still must.
    let files = MemoryFiles::default();
    files.profiles(1);
    let storage = Storage::open(Box::new(files.clone()), SID).unwrap();
    storage
        .save(Some(serde_json::from_value(value(2, 0)).unwrap()), 0)
        .unwrap();
    let bytes = || files.read("manual-overrides.json").unwrap();
    let before = bytes();
    let mut seen = vec![storage.policy_inputs().unwrap()];
    let changed = |seen: &mut Vec<[u8; 32]>| {
        assert_eq!(bytes(), before, "only a temporary may change");
        let next = storage.policy_inputs().unwrap();
        assert!(!seen.contains(&next));
        seen.push(next);
    };
    // Another temporary performance value.
    storage
        .save(Some(serde_json::from_value(value(1, 0)).unwrap()), 0)
        .unwrap();
    changed(&mut seen);
    // A temporary fan override.
    storage
        .save_fan(
            Some(crate::service_status::FanOverride {
                value: 1,
                lifetime: 0,
            }),
            Some(crate::installed::readiness::thermal_storage::legacy_fan(1)),
            0,
        )
        .unwrap();
    changed(&mut seen);
    // Both expire on another class.
    storage.observe_power(2, 1).unwrap();
    changed(&mut seen);
}
async fn fresh_observe(rig: &Rig) -> Value {
    assert_eq!(rig.get().await["success"], true); // Same metadata readiness probe as installed prepare.
    rig.request(json!({"operation":"observeState","version":91,"requestId":"eeeeeeee-bbbb-cccc-dddd-000000000001","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})).await
}
#[tokio::test]
async fn correction_manual_active0581_stores_without_setters_and_automatic_stays_direct() {
    // Full (15/15) and limited (10/15) adapters both defer, as managed does.
    for (selector, actual) in [(4, 15), (2, 15), (0, 15), (4, 10), (2, 10), (0, 10)] {
        let rig = Rig::new(manual_files());
        {
            let mut state = rig.port.state.lock().unwrap();
            state.selectors = [selector, selector];
            state.actual = actual;
        }
        let before = fresh_observe(&rig).await;
        assert_eq!(before["status"]["confirmedState"]["fans"], 3);
        let response = rig.set(91, value(1, 1)).await;
        assert_eq!(response["result"]["status"], 0);
        assert!(
            response["result"]["message"]
                .as_str()
                .unwrap()
                .contains("Automatic")
        );
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        assert_eq!(
            rig.files.document("manual-overrides.json")["overrides"]["performance"],
            1
        );
        let after = fresh_observe(&rig).await;
        assert_eq!(
            after["status"]["confirmedState"],
            before["status"]["confirmedState"]
        );
        assert!(after["status"]["confirmedState"]["customPerformance"].is_null());
        rig.stop().await;
    }
    let rig = Rig::new(manual_files());
    {
        let mut state = rig.port.state.lock().unwrap();
        state.selectors = [2, 2];
        state.fans = [0, 0];
    }
    assert_eq!(rig.set(92, value(1, 1)).await["result"]["status"], 1);
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["fanControl"]["fixedSpeedRpm"],
        3700
    );
    rig.stop().await;
}
#[tokio::test]
async fn correction_transient_fresh_refusal_keeps_endpoint_and_reader_available() {
    let rig = Rig::new(MemoryFiles::default());
    assert_eq!(fresh_observe(&rig).await["success"], true);
    rig.custody.fresh_refuse.store(true, Ordering::Release);
    let refused = rig.set(91, value(1, 1)).await;
    assert_eq!(refused["result"]["status"], 2);
    assert!(
        refused["result"]["message"]
            .as_str()
            .unwrap()
            .contains("Current thermal admission")
    );
    assert_eq!(rig.get().await["success"], true);
    assert_eq!(fresh_observe(&rig).await["success"], true);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
    assert!(
        rig.files
            .read("thermal-mutation.journal")
            .unwrap()
            .is_none()
    );
    rig.custody.fresh_refuse.store(false, Ordering::Release);
    assert_eq!(rig.set(92, value(1, 1)).await["result"]["status"], 1);
    rig.stop().await;
}
#[tokio::test]
async fn correction_clean_activation_refusal_reconstructs_only_after_real_read_drain() {
    for refusal in [1, 2] {
        let rig = Rig::new(MemoryFiles::default());
        assert_eq!(fresh_observe(&rig).await["success"], true);
        let before = rig.port.state.lock().unwrap().opened;
        rig.custody
            .activation_refusal
            .store(refusal, Ordering::Release);
        assert_eq!(rig.set(91, value(1, 1)).await["result"]["status"], 2);
        let observed = fresh_observe(&rig).await;
        {
            let state = rig.port.state.lock().unwrap();
            assert_eq!(state.writes, 0);
            assert_eq!(state.opened, state.retired);
            assert_eq!(state.opened > before, refusal == 1);
            if refusal == 1 {
                assert_eq!(observed["success"], true);
            }
        }
        assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
        rig.stop().await;
    }
}
#[tokio::test]
async fn correction_prepared_only_settles_after_drain_and_allows_fresh_request() {
    let rig = Rig::new(MemoryFiles::default());
    rig.port.state.lock().unwrap().change_power = Some((2, 0));
    let response = rig.set(91, value(1, 1)).await;
    assert_eq!(response["result"]["status"], 2);
    assert!(
        response["result"]["message"]
            .as_str()
            .unwrap()
            .contains("power source changed")
    );
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    let bytes = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
    let entries: Vec<Value> = bytes
        .split(|b| *b == b'\n')
        .filter(|v| !v.is_empty())
        .map(|v| serde_json::from_slice(v).unwrap())
        .collect();
    assert_eq!(
        entries
            .iter()
            .map(|v| v["progress"].as_u64().unwrap())
            .collect::<Vec<_>>(),
        [0, 6]
    );
    let reopened = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
    assert!(!reopened.pending());
    assert_eq!(reopened.progress(), Some(6));
    {
        let mut state = rig.port.state.lock().unwrap();
        assert_eq!(state.opened, state.retired);
        state.actual = 15;
        state.change_power = None;
    }
    assert_eq!(rig.set(92, value(1, 1)).await["result"]["status"], 1);
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    let later = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
    assert!(later.starts_with(&bytes));
    rig.stop().await;
}
#[tokio::test]
async fn correction_storage_failure_and_postacceptance_capacity_have_truthful_causes() {
    let rig = Rig::new(MemoryFiles::default());
    rig.files.fail.store(true, Ordering::Release);
    for n in [91, 92] {
        let response = rig.set(n, value(1, 1)).await;
        assert_eq!(response["result"]["status"], 2);
        let message = response["result"]["message"].as_str().unwrap();
        assert!(message.contains("configuration replacement failed"));
        assert!(!message.contains("may have been submitted"));
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
    }
    rig.stop().await;
    let rig = Rig::new(MemoryFiles::default());
    // Eight free IDs: the Custom baseline read frees two of its four once
    // answered, and the power read keeps two, so the accepted recipe cannot
    // reserve the rest.
    rig.port.reserve_for_tests(123);
    let response = rig.set(91, value(1, 1)).await;
    assert_eq!(response["result"]["status"], 2);
    assert!(
        response["result"]["message"]
            .as_str()
            .unwrap()
            .contains("transaction ID capacity")
    );
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["performance"],
        1
    );
    assert!(
        rig.files
            .read("thermal-mutation.journal")
            .unwrap()
            .is_none()
    );
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.stop().await;
}
#[tokio::test]
async fn correction_fresh_metadata_expires_temporary_policy_without_promoting_private_facts() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let rig = Rig::new(files);
    assert_eq!(rig.set(91, value(2, 0)).await["result"]["status"], 1);
    assert_eq!(rig.get().await["status"]["performanceOverride"]["value"], 2);
    rig.metadata.store(1, Ordering::Release);
    // Fresh Windows power agrees with the battery base (`status_query_power`).
    rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
    let status = rig.get().await;
    assert_eq!(status["status"]["activePowerSource"], 1);
    assert!(status["status"]["performanceOverride"].is_null());
    assert_eq!(status["status"]["writesBlocked"], true);
    rig.metadata.store(3, Ordering::Release);
    let status = rig.get().await;
    assert_eq!(status["status"]["deviceVerified"], false);
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    rig.stop().await;
}
#[tokio::test]
async fn correction_late_accepted_intent_reconciles_only_with_new_read_without_replay() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let gate = RetirementGate::install(&rig);
    let request=crate::service_status::StatusRequest::decode(&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000091","value":value(1,1)})).unwrap()).unwrap();
    // The worker caps the deadline at the 6 s recipe budget. The recipe runs
    // on paused fixture time and reaches retirement well inside it, however
    // loaded the host is. Paused time then expires the deadline while the
    // worker is parked, with no real wait.
    let deadline = Instant::now() + std::time::Duration::from_secs(6);
    let operation = rig
        .source
        .lock()
        .await
        .mutate(USER, SID.into(), request, deadline);
    tokio::pin!(operation);
    tokio::select! {_=&mut operation=>panic!("retirement unexpectedly completed"),()=gate.reached()=>{}}
    tokio::time::pause();
    tokio::time::advance(deadline.saturating_duration_since(Instant::now())).await;
    // The worker finishes retirement and serves later reads on OS threads, so
    // the rest of the test waits on the real clock.
    tokio::time::resume();
    gate.open();
    let (result, status) = operation.await.unwrap();
    assert_eq!(result.status, 2);
    // Retirement is past, so the later reads park nothing and run on a held clock.
    rig.port.state.lock().unwrap().on_mutation_retired = None;
    assert!(status.is_none());
    let count = rig.port.state.lock().unwrap().sent.len();
    let observed = fresh_observe(&rig).await;
    assert_eq!(observed["status"]["performanceOverride"]["value"], 1);
    assert_eq!(observed["status"]["confirmedState"]["performance"], 1);
    {
        let state = rig.port.state.lock().unwrap();
        assert!(state.sent.len() > count);
        assert_eq!(state.writes, 2);
    }
    rig.stop().await;
}
#[tokio::test]
async fn correction_lifecycle_fences_inflight_epoch_without_destroying_metadata_origin() {
    use windows_sys::Win32::System::Services::{
        SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE, SERVICE_CONTROL_STOP,
    };
    let rig = Rig::new(MemoryFiles::default());
    let controls = crate::scm_host::Controls::new();
    let mut stop = controls.arm();
    assert_eq!(fresh_observe(&rig).await["success"], true);
    let initial = ReadCustody::authorize(rig.custody.as_ref()).unwrap();
    rig.custody.lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 4);
    assert_eq!(controls.handle(SERVICE_CONTROL_POWEREVENT), 0);
    assert!(ReadCustody::authorize(rig.custody.as_ref()).is_none());
    let before = rig.port.state.lock().unwrap().sent.len();
    assert_eq!(rig.get().await["success"], true);
    fresh_observe(&rig).await;
    assert_eq!(rig.port.state.lock().unwrap().sent.len(), before);
    assert!(stop.try_recv().is_err());
    rig.custody
        .lifecycle
        .ingress(SERVICE_CONTROL_SESSIONCHANGE, 8);
    assert!(ReadCustody::authorize(rig.custody.as_ref()).is_none());
    rig.custody
        .lifecycle
        .ingress(SERVICE_CONTROL_POWEREVENT, 18);
    assert_ne!(
        ReadCustody::authorize(rig.custody.as_ref()).unwrap(),
        initial
    );
    assert_eq!(fresh_observe(&rig).await["success"], true);
    assert_eq!(controls.handle(SERVICE_CONTROL_STOP), 0);
    assert_eq!(stop.try_recv(), Ok(()));
    rig.stop().await;
}

#[tokio::test]
async fn correction_queued_stored_subscription_rebases_latest_hardware_without_renewing_timestamp()
{
    use std::future::Future;
    let rig = Rig::new(MemoryFiles::default());
    fresh_observe(&rig).await;
    let release = Arc::new(AtomicBool::new(false));
    rig.port.state.lock().unwrap().held_write = Some((1, release.clone()));
    let (mut client, server) = duplex(65536);
    let source = rig.source.clone();
    let publications = source.lock().await.publications();
    let server = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }),
        source,
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let exercise = async {
        crate::service_frame::write_payload(&mut client,&serde_json::to_vec(&json!({"operation":"subscribeStatus","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000090"})).unwrap()).await.unwrap();
        let _: Value = serde_json::from_slice(
            &crate::service_frame::read_payload(&mut client)
                .await
                .unwrap(),
        )
        .unwrap();
        let a = rig.set(91, value(1, 1));
        tokio::pin!(a);
        let entered = async {
            while rig.port.state.lock().unwrap().writes != 1 {
                tokio::task::yield_now().await;
            }
        };
        tokio::select! {_=&mut a=>panic!("held A completed"),v=tokio::time::timeout(HANG_GUARD,entered)=>v.unwrap()}
        let mut desired = value(2, 1);
        desired["gpuClockTuning"] =
            json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
        let request=crate::service_status::StatusRequest::decode(&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000092","value":desired})).unwrap()).unwrap();
        let b = rig.source.lock().await.mutate(
            USER,
            SID.into(),
            request,
            Instant::now() + std::time::Duration::from_secs(3),
        );
        tokio::pin!(b);
        std::future::poll_fn(|cx| {
            assert!(b.as_mut().poll(cx).is_pending());
            Poll::Ready(())
        })
        .await;
        assert_eq!(
            rig.entered.get(),
            2,
            "B captured its pre-A base and queued at the runtime gate"
        );
        release.store(true, Ordering::Release);
        assert_eq!(a.await["result"]["status"], 1);
        let event_a: Value = serde_json::from_slice(
            &crate::service_frame::read_payload(&mut client)
                .await
                .unwrap(),
        )
        .unwrap();
        assert_eq!(event_a["status"]["confirmedState"]["performance"], 1);
        let timestamp = rig.reader.last_completed_for_tests();
        assert!(timestamp.is_some());
        let (result, _) = b.await.unwrap();
        assert_eq!(result.status, 2);
        let event_b: Value = serde_json::from_slice(
            &crate::service_frame::read_payload(&mut client)
                .await
                .unwrap(),
        )
        .unwrap();
        assert_eq!(
            event_b["status"]["confirmedState"],
            event_a["status"]["confirmedState"]
        );
        assert!(
            event_b["status"]["revision"].as_u64().unwrap()
                > event_a["status"]["revision"].as_u64().unwrap()
        );
        assert_eq!(event_b["status"]["performanceOverride"]["value"], 2);
        assert_eq!(rig.reader.last_completed_for_tests(), timestamp);
        assert_eq!(rig.port.state.lock().unwrap().writes, 2);
        drop(client);
    };
    let (result, ()) = tokio::join!(server, exercise);
    assert!(result.is_ok());
    rig.stop().await;
}
#[tokio::test]
async fn correction_unknown_external_metadata_preserves_temporary_intent_without_claiming_full_power()
 {
    let rig = Rig::new(MemoryFiles::default());
    assert_eq!(rig.set(91, value(2, 0)).await["result"]["status"], 1);
    for operation in ["getStatus", "observeState", "subscribeStatus"] {
        let mut request = json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000093"});
        if operation == "observeState" {
            request["includeLiveReadings"] = json!(false);
            request["requestedLiveReadings"] = json!(0);
            request["effectiveLiveReadings"] = json!(0);
        }
        let response = rig.request(request).await;
        assert_eq!(response["status"]["performanceOverride"]["value"], 2);
        assert_eq!(response["status"]["activePowerSource"], 3);
        assert_eq!(response["status"]["writesBlocked"], true);
    }
    rig.metadata.store(1, Ordering::Release);
    // Fresh Windows power agrees with the battery base (`status_query_power`).
    rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
    assert!(rig.get().await["status"]["performanceOverride"].is_null());
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    rig.stop().await;
}

async fn outer_request(tx: &mpsc::UnboundedSender<Box<dyn Stream>>, request: Value) -> Value {
    let (mut client, server) = duplex(65536);
    tx.send(Box::new(SidStream {
        inner: test_stream(server, USER, Fault::None),
        sid: SID.into(),
    }))
    .unwrap();
    crate::service_frame::write_payload(&mut client, &serde_json::to_vec(&request).unwrap())
        .await
        .unwrap();
    let bytes = crate::service_frame::read_payload(&mut client)
        .await
        .unwrap();
    emit_fixture_frame(
        "peer_response",
        request["operation"].as_str().unwrap(),
        "response",
        &bytes,
    );
    println!(
        "NATIVE_THERMAL_EDGE_TRACE {} {}",
        request["operation"].as_str().unwrap(),
        std::str::from_utf8(&bytes).unwrap()
    );
    serde_json::from_slice(&bytes).unwrap()
}
#[tokio::test]
async fn correction_outer_lifecycle_and_transient_admission_keep_service_running_until_stop() {
    use windows_sys::Win32::System::Services::{
        SERVICE_CONTROL_POWEREVENT, SERVICE_CONTROL_SESSIONCHANGE, SERVICE_CONTROL_STOP,
    };
    let rig = Rig::new(MemoryFiles::default());
    let events: Events = Rc::default();
    let controls = Controls::new();
    let (mut ports, tx) = ports(Fault::None, &events);
    ports.source = rig.source.clone();
    let (running, ready) = oneshot::channel();
    let service = lifecycle(&controls, Ok(ports), &events, move || {
        running.send(()).unwrap();
    });
    let exercise = async {
        ready.await.unwrap();
        let ingress = |code, event| {
            crate::scm_host::installed_ingress(&rig.custody.lifecycle, code, event, || {
                rig.origin.stop()
            });
            assert_eq!(controls.handle(code), 0);
        };
        let get = || json!({"operation":"getStatus","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000094"});
        let observe = || json!({"operation":"observeState","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000095","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0});
        assert_eq!(outer_request(&tx, observe()).await["success"], true);
        rig.custody.fresh_refuse.store(true, Ordering::Release);
        let refused=outer_request(&tx,json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096","value":value(1,1)})).await;
        assert_eq!(refused["result"]["status"], 2);
        assert_eq!(outer_request(&tx, get()).await["success"], true);
        ingress(SERVICE_CONTROL_POWEREVENT, 4);
        assert!(rig.origin.verify().is_ok());
        let count = rig.port.state.lock().unwrap().sent.len();
        assert_eq!(outer_request(&tx, get()).await["success"], true);
        outer_request(&tx, observe()).await;
        assert_eq!(rig.port.state.lock().unwrap().sent.len(), count);
        ingress(SERVICE_CONTROL_SESSIONCHANGE, 8);
        assert!(rig.origin.verify().is_ok());
        assert!(ReadCustody::authorize(rig.custody.as_ref()).is_none());
        ingress(SERVICE_CONTROL_POWEREVENT, 18);
        assert!(rig.origin.verify().is_ok());
        rig.custody.fresh_refuse.store(false, Ordering::Release);
        assert_eq!(outer_request(&tx, observe()).await["success"], true);
        assert!(!events.borrow().contains(&"stopping"));
        assert!(!events.borrow().contains(&"failed"));
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        ingress(SERVICE_CONTROL_STOP, 0);
        assert!(crate::loaded_origin::lifetime::expected_stop(
            &rig.origin.verify().unwrap_err()
        ));
    };
    let (result, ()) = tokio::join!(service, exercise);
    result.unwrap();
    assert!(events.borrow().contains(&"stopped"));
    assert!(!events.borrow().contains(&"failed"));
    assert!(rig.custody.retired.load(Ordering::Acquire));
}

#[tokio::test]
async fn correction_prepared_settlement_waits_for_actual_retirement_and_rejects_foreign_prefix() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let entered = Arc::new(AtomicBool::new(false));
    let released = Arc::new(AtomicBool::new(false));
    let flag = entered.clone();
    let gate = released.clone();
    {
        let mut s = rig.port.state.lock().unwrap();
        s.change_power = Some((2, 0));
        s.on_mutation_retired = Some(Arc::new(move || {
            flag.store(true, Ordering::Release);
            while !gate.load(Ordering::Acquire) {
                std::thread::yield_now();
            }
        }));
    }
    let request = rig.set(91, value(1, 1));
    tokio::pin!(request);
    let reached = async {
        while !entered.load(Ordering::Acquire) {
            tokio::task::yield_now().await;
        }
    };
    tokio::select! {_=&mut request=>panic!("unretired operation completed"),r=tokio::time::timeout(HANG_GUARD,reached)=>r.unwrap()}
    let prepared = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
    let prior = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
    assert_eq!(prior.progress(), Some(0));
    assert!(prior.pending());
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    released.store(true, Ordering::Release);
    assert_eq!(request.await["result"]["status"], 2);
    let settled = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
    assert!(settled.starts_with(&prepared));
    let mut lines: Vec<Value> = settled
        .split(|b| *b == b'\n')
        .filter(|v| !v.is_empty())
        .map(|v| serde_json::from_slice(v).unwrap())
        .collect();
    for field in ["request", "owner", "user_sid"] {
        let mut changed = lines.clone();
        changed[1][field] = json!(if field == "request" {
            "aaaaaaaa-bbbb-cccc-dddd-000000000099"
        } else if field == "owner" {
            "1111111111111111111111111111111111111111111111111111111111111111"
        } else {
            "S-1-5-21-111-222-333-1002"
        });
        let mut bytes = Vec::new();
        for line in changed {
            bytes.extend(serde_json::to_vec(&line).unwrap());
            bytes.push(b'\n');
        }
        let files = MemoryFiles::default();
        files
            .bytes
            .lock()
            .unwrap()
            .insert("thermal-mutation.journal".into(), bytes.clone());
        assert!(Storage::open(Box::new(files.clone()), SID).is_err());
        assert_eq!(
            files.read("thermal-mutation.journal").unwrap().unwrap(),
            bytes
        );
    }
    let mut first_intent = lines[0].clone();
    first_intent["progress"] = json!(1);
    lines.insert(1, first_intent); // Valid Prepared -> FirstIntent prefix; only 1 -> 6 is forbidden.
    let files = MemoryFiles::default();
    let mut bytes = Vec::new();
    for line in lines {
        bytes.extend(serde_json::to_vec(&line).unwrap());
        bytes.push(b'\n');
    }
    files
        .bytes
        .lock()
        .unwrap()
        .insert("thermal-mutation.journal".into(), bytes);
    assert!(Storage::open(Box::new(files), SID).is_err());
    rig.stop().await;
}

#[tokio::test]
async fn correction_outer_fatal_admission_terminates_and_preserves_possible_submission() {
    for mode in [0, 1, 2] {
        let after_submission = mode == 1;
        let rig = Rig::new(MemoryFiles::default());
        let events: Events = Rc::default();
        let controls = Controls::new();
        if after_submission {
            let custody = rig.custody.clone();
            let previous = rig.port.state.lock().unwrap().before_write.take().unwrap();
            rig.port.state.lock().unwrap().before_write = Some(Arc::new(move |number| {
                previous(number);
                custody.fatal_health.store(true, Ordering::Release);
            }));
        } else {
            if mode == 2 {
                rig.fatal_capture.store(true, Ordering::Release);
            } else {
                rig.custody.fatal_health.store(true, Ordering::Release);
            }
        }
        let (mut ports, tx) = ports(Fault::None, &events);
        ports.source = rig.source.clone();
        let (running, ready) = oneshot::channel();
        let service = lifecycle(&controls, Ok(ports), &events, move || {
            running.send(()).unwrap();
        });
        let exercise = async {
            ready.await.unwrap();
            let (mut client, server) = duplex(65536);
            tx.send(Box::new(SidStream {
                inner: test_stream(server, USER, Fault::None),
                sid: SID.into(),
            }))
            .unwrap();
            crate::service_frame::write_payload(&mut client,&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})).unwrap()).await.unwrap();
            assert!(
                crate::service_frame::read_payload(&mut client)
                    .await
                    .is_err()
            );
        };
        let (result, ()) = tokio::join!(service, exercise);
        assert!(result.is_err());
        assert!(events.borrow().contains(&"failed"));
        assert!(rig.custody.retired.load(Ordering::Acquire));
        if after_submission {
            assert_eq!(rig.port.state.lock().unwrap().writes, 1);
            let bytes = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
            let storage = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
            assert!(storage.pending());
            assert_eq!(storage.progress(), Some(1));
            assert_eq!(
                rig.files.read("thermal-mutation.journal").unwrap().unwrap(),
                bytes
            );
        } else {
            assert_eq!(rig.port.state.lock().unwrap().writes, 0);
            assert!(rig.files.read("manual-overrides.json").unwrap().is_none());
            assert!(
                rig.files
                    .read("thermal-mutation.journal")
                    .unwrap()
                    .is_none()
            );
        }
    }
}

#[tokio::test]
async fn edge_configuration_drift_keeps_public_read_proof_and_truthful_cause() {
    let rig = Rig::new(MemoryFiles::default());
    let mut desired = value(2, 1);
    desired["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
    assert_eq!(rig.set(91, desired).await["result"]["status"], 2);
    let before = fresh_observe(&rig).await;
    let mut foreign = rig.files.document("manual-overrides.json");
    foreign["overrides"]["performance"] = json!(1);
    rig.files.put("manual-overrides.json", foreign);
    let bytes = rig.files.read("manual-overrides.json").unwrap();
    let after = fresh_observe(&rig).await;
    assert_eq!(after["success"], true);
    assert_eq!(
        after["status"]["confirmedState"],
        before["status"]["confirmedState"]
    );
    assert!(after["status"]["performanceOverride"].is_null());
    assert_eq!(after["status"]["writesBlocked"], true);
    assert!(
        after["status"]["message"]
            .as_str()
            .unwrap()
            .contains("private policy")
    );
    assert!(
        !after["status"]["message"]
            .as_str()
            .unwrap()
            .contains("permission is revoked")
    );
    let observed = rig.observe(Duration::from_secs(2)).await.unwrap().unwrap();
    assert!(observed.has_fresh_mailbox_read());
    let stamp = rig.reader.last_completed_for_tests();
    assert!(
        rig.reader
            .current_policy(observed.clone())
            .unwrap()
            .has_fresh_mailbox_read()
    );
    tokio::time::pause();
    tokio::time::advance(std::time::Duration::from_secs(3)).await;
    assert!(
        !rig.reader
            .current_policy(observed)
            .unwrap()
            .has_fresh_mailbox_read()
    );
    assert_eq!(rig.reader.last_completed_for_tests(), stamp);
    tokio::time::resume();
    let refused = rig.set(92, value(1, 1)).await;
    assert!(
        refused["result"]["message"]
            .as_str()
            .unwrap()
            .contains("configuration changed")
    );
    assert!(
        !refused["result"]["message"]
            .as_str()
            .unwrap()
            .contains("earlier thermal submission")
    );
    assert_eq!(rig.files.read("manual-overrides.json").unwrap(), bytes);
    assert!(
        rig.files
            .read("thermal-mutation.journal")
            .unwrap()
            .is_none()
    );
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.stop().await;
}

#[tokio::test]
async fn edge_terminal_settlement_failure_recovers_reads_without_retry_or_writes() {
    let rig = Rig::new(MemoryFiles::default());
    let retired = Arc::new(AtomicBool::new(false));
    let flag = retired.clone();
    let files = rig.files.clone();
    {
        let mut port = rig.port.state.lock().unwrap();
        port.change_power = Some((2, 0));
        port.on_mutation_retired = Some(Arc::new(move || {
            let prepared = files.read("thermal-mutation.journal").unwrap().unwrap();
            assert_eq!(
                serde_json::from_slice::<Value>(prepared.strip_suffix(b"\n").unwrap()).unwrap()["progress"],
                0
            );
            files.fail_terminal.store(true, Ordering::Release);
            flag.store(true, Ordering::Release);
        }));
    }
    let result = rig.set(91, value(1, 1)).await;
    assert_eq!(result["result"]["status"], 2);
    let message = result["result"]["message"].as_str().unwrap();
    assert!(message.contains("no mutation submission intent was reached"));
    assert!(!message.contains("submission-intent journal publication could not be confirmed"));
    assert!(retired.load(Ordering::Acquire));
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 1);
    let prepared = rig.files.read("thermal-mutation.journal").unwrap();
    let pending = rig.files.read("thermal-mutation.journal.pending").unwrap();
    assert!(pending.is_some());
    let opened = rig.port.state.lock().unwrap().opened;
    assert_eq!(fresh_observe(&rig).await["success"], true);
    assert!(rig.port.state.lock().unwrap().opened > opened);
    assert_eq!(rig.set(92, value(2, 1)).await["result"]["status"], 2);
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 1);
    assert_eq!(
        rig.files.read("thermal-mutation.journal").unwrap(),
        prepared
    );
    assert_eq!(
        rig.files.read("thermal-mutation.journal.pending").unwrap(),
        pending
    );
    assert!(Storage::open(Box::new(rig.files.clone()), "S-1-5-21-111-222-333-1002").is_err());
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.stop().await;
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 1);
    assert_eq!(
        rig.files.read("thermal-mutation.journal").unwrap(),
        prepared
    );
    assert_eq!(
        rig.files.read("thermal-mutation.journal.pending").unwrap(),
        pending
    );
}

#[tokio::test]
async fn edge_manual_stored_requires_new_explicit_request_after_automatic() {
    let rig = Rig::new(manual_files());
    let stored = rig.set(91, value(1, 1)).await;
    assert_eq!(stored["result"]["status"], 0);
    let message = stored["result"]["message"].as_str().unwrap();
    // With the fan composition a fan request that returns the fans to Automatic
    // applies it; observing firmware back in Automatic never does.
    if cfg!(feature = "service-fan-mutation-v2") {
        assert_eq!(
            message,
            "Performance intent was stored and will be applied when fans return to Automatic."
        );
    } else {
        assert!(
            message.contains("saved, not applied") && message.contains("new performance request")
        );
    }
    {
        let mut port = rig.port.state.lock().unwrap();
        port.selectors = [2, 2];
        port.fans = [0, 0];
    }
    assert_eq!(fresh_observe(&rig).await["success"], true);
    rig.get().await;
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    assert!(
        rig.files
            .read("thermal-mutation.journal")
            .unwrap()
            .is_none()
    );
    assert_eq!(rig.set(92, value(1, 1)).await["result"]["status"], 1);
    assert_eq!(rig.port.state.lock().unwrap().writes, 2);
    rig.stop().await;
}

#[tokio::test]
async fn edge_pre_activation_cancellation_and_suspend_recover_only_after_callback_retirement() {
    for suspend in [false, true] {
        let rig = Rig::new(MemoryFiles::default());
        rig.get().await;
        let release = Arc::new(AtomicBool::new(false));
        rig.port.state.lock().unwrap().held_read = Some(release.clone());
        {
            let observation = rig
                .source
                .lock()
                .await
                .observe_performance(USER, Instant::now() + std::time::Duration::from_secs(5));
            tokio::pin!(observation);
            let opened = async {
                while !rig
                    .port
                    .state
                    .lock()
                    .unwrap()
                    .sent
                    .iter()
                    .any(|(class, command, _, _)| *class == 13 && *command == 0x82)
                {
                    tokio::task::yield_now().await;
                }
            };
            tokio::select! { _ = &mut observation => panic!("held getter completed"), r = tokio::time::timeout(HANG_GUARD, opened) => r.unwrap() }
        } // Cancel the actual read future, leaving its native callback held.
        {
            let request = rig.set(91, value(1, 1));
            tokio::pin!(request);
            let entered = async {
                while rig.entered.get() == 0 {
                    tokio::task::yield_now().await;
                }
            };
            tokio::select! { _ = &mut request => panic!("request escaped held pre-activation drain"), r = tokio::time::timeout(HANG_GUARD, entered) => r.unwrap() }
            assert_eq!(rig.entered.get(), 1);
            assert_eq!(rig.port.state.lock().unwrap().opened, 1);
            assert_eq!(rig.port.state.lock().unwrap().retired, 0);
            assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
            if suspend {
                rig.custody.lifecycle.ingress(13, 4);
                release.store(true, Ordering::Release);
                assert_eq!(request.await["result"]["status"], 2);
            }
        } // Other arm cancels while pre-activation stop is pending.
        release.store(true, Ordering::Release);
        if suspend {
            rig.custody.lifecycle.ingress(13, 7);
        }
        assert_eq!(fresh_observe(&rig).await["success"], true);
        {
            let state = rig.port.state.lock().unwrap();
            assert_eq!(state.opened, state.retired);
            assert!(state.opened > 1);
            assert_eq!(state.writes, 0);
        }
        assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        rig.stop().await;
    }
}

#[tokio::test]
async fn edge_outer_policy_fatal_capture_and_health_fail_get_and_observe() {
    for (capture, observe) in [(true, false), (false, false), (true, true), (false, true)] {
        let rig = Rig::new(MemoryFiles::default());
        let mut saved = value(1, 1);
        saved["gpuClockTuning"] =
            json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
        assert_eq!(rig.set(91, saved).await["result"]["status"], 2);
        let journal = rig.files.read("thermal-mutation.journal").unwrap();
        let events: Events = Rc::default();
        let controls = Controls::new();
        let (mut ports, tx) = ports(Fault::None, &events);
        let entered = Rc::new(std::cell::Cell::new(false));
        let flag = if capture {
            rig.fatal_capture.clone()
        } else {
            Arc::new(AtomicBool::new(false))
        };
        ports.source = Rc::new(Mutex::new(Box::new(RouteFatalSource {
            inner: rig.source.clone(),
            publications: rig.source.lock().await.publications(),
            observe,
            entered: entered.clone(),
            capture: flag,
            health: if capture {
                None
            } else {
                Some(rig.custody.clone())
            },
        })));
        let (running, ready) = oneshot::channel();
        let service = lifecycle(&controls, Ok(ports), &events, move || {
            running.send(()).unwrap();
        });
        let exercise = async {
            ready.await.unwrap();
            assert!(!entered.get());
            let (mut client, server) = duplex(65536);
            tx.send(Box::new(SidStream {
                inner: test_stream(server, USER, Fault::None),
                sid: SID.into(),
            }))
            .unwrap();
            let request = if observe {
                json!({"operation":"observeState","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
            } else {
                json!({"operation":"getStatus","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098"})
            };
            crate::service_frame::write_payload(
                &mut client,
                &serde_json::to_vec(&request).unwrap(),
            )
            .await
            .unwrap();
            assert!(
                crate::service_frame::read_payload(&mut client)
                    .await
                    .is_err()
            );
        };
        let (result, ()) = rig
            .held(
                HeldClock::CatchUp,
                Box::pin(async { tokio::join!(service, exercise) }),
            )
            .await;
        assert!(
            entered.get(),
            "the requested Get/Observe route must arm policy failure"
        );
        assert!(result.is_err());
        assert!(events.borrow().contains(&"failed"));
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert_eq!(rig.files.read("thermal-mutation.journal").unwrap(), journal);
    }
}

// Arm only from the actual requested route. Once armed, injected background
// refresh is held so it cannot satisfy the requested fatal-dispatch assertion.
struct RouteFatalSource {
    inner: Rc<Mutex<Box<dyn StatusSource>>>,
    publications: Option<watch::Receiver<Arc<Publication>>>,
    observe: bool,
    entered: Rc<std::cell::Cell<bool>>,
    capture: Arc<AtomicBool>,
    health: Option<Arc<Custody>>,
}
impl RouteFatalSource {
    fn arm(&self) {
        assert!(!self.entered.replace(true));
        if let Some(health) = &self.health {
            health.fatal_health.store(true, Ordering::Release);
        } else {
            self.capture.store(true, Ordering::Release);
        }
    }
}
impl StatusSource for RouteFatalSource {
    fn snapshot(&mut self, caller: Caller, requested: Instant) -> Pending<'_, Snapshot> {
        if !self.observe && caller.logon == Logon::Interactive {
            self.arm();
        }
        Box::pin(async move { self.inner.lock().await.snapshot(caller, requested).await })
    }
    fn observe_performance(&mut self, caller: Caller, deadline: Instant) -> Observation {
        assert!(self.observe);
        self.arm();
        let inner = self.inner.clone();
        Box::pin(async move {
            let observation = inner.lock().await.observe_performance(caller, deadline);
            observation.await
        })
    }
    fn publications(&self) -> Option<watch::Receiver<Arc<Publication>>> {
        self.publications.clone()
    }
    fn refresh_publications(&mut self) -> Pending<'_, Instant> {
        Box::pin(async move {
            if self.entered.get() {
                Ok(Instant::now() + std::time::Duration::from_secs(60))
            } else {
                self.inner.lock().await.refresh_publications().await
            }
        })
    }
    fn drain(&mut self) -> Pending<'_, ()> {
        Box::pin(async move { self.inner.lock().await.drain().await })
    }
}

#[tokio::test]
async fn edge_original_owner_integrity_loss_after_retirement_never_reopens_reads() {
    let rig = Rig::new(MemoryFiles::default());
    let custody = rig.custody.clone();
    {
        let mut port = rig.port.state.lock().unwrap();
        port.change_power = Some((2, 0));
        port.on_mutation_retired = Some(Arc::new(move || {
            custody.integrity_lost.store(true, Ordering::Release)
        }));
    }
    rig.get().await;
    let request = crate::service_status::StatusRequest::decode(&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})).unwrap()).unwrap();
    let result = rig
        .source
        .lock()
        .await
        .mutate(
            USER,
            SID.into(),
            request,
            Instant::now() + std::time::Duration::from_secs(3),
        )
        .await;
    assert!(result.is_err());
    let opened = rig.port.state.lock().unwrap().opened;
    let observation = rig
        .source
        .lock()
        .await
        .observe_performance(USER, Instant::now() + std::time::Duration::from_secs(3));
    assert!(observation.await.is_err());
    assert_eq!(rig.port.state.lock().unwrap().opened, opened);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
    let journal = rig.files.read("thermal-mutation.journal").unwrap();
    rig.stop().await;
    assert_eq!(rig.files.read("thermal-mutation.journal").unwrap(), journal);
}

#[tokio::test]
async fn edge_stored_temporary_publication_cannot_resurrect_expired_policy() {
    let rig = Rig::new(MemoryFiles::default());
    let observed = fresh_observe(&rig).await;
    let timestamp = rig.reader.last_completed_for_tests();
    let retired = Arc::new(AtomicBool::new(false));
    let release = Arc::new(AtomicBool::new(false));
    let flag = retired.clone();
    let gate = release.clone();
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        flag.store(true, Ordering::Release);
        while !gate.load(Ordering::Acquire) {
            std::thread::yield_now();
        }
    }));
    let (mut client, server) = duplex(65536);
    let source = rig.source.clone();
    let publications = source.lock().await.publications();
    let server = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }),
        source,
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let exercise = async {
        crate::service_frame::write_payload(&mut client, &serde_json::to_vec(&json!({"operation":"subscribeStatus","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000090"})).unwrap()).await.unwrap();
        crate::service_frame::read_payload(&mut client)
            .await
            .unwrap();
        let mut desired = value(2, 0);
        desired["gpuClockTuning"] =
            json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
        let request = rig.set(91, desired);
        tokio::pin!(request);
        let held = async {
            while !retired.load(Ordering::Acquire) {
                tokio::task::yield_now().await;
            }
        };
        tokio::select! { _ = &mut request => panic!("retirement must remain held"), result = tokio::time::timeout(HANG_GUARD, held) => result.unwrap() }
        assert_eq!(
            rig.files.document("manual-overrides.json")["overrides"]["performance"],
            Value::Null
        );
        rig.metadata.store(1, Ordering::Release);
        // Fresh Windows power agrees with the battery base (`status_query_power`).
        rig.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
        let expired = rig.get().await;
        assert_eq!(expired["status"]["activePowerSource"], 1);
        assert!(expired["status"]["performanceOverride"].is_null());
        // Consume the expiry publication, then the queued Stored publication,
        // without a repairing Get between them.
        let expiry: Value = serde_json::from_slice(
            &crate::service_frame::read_payload(&mut client)
                .await
                .unwrap(),
        )
        .unwrap();
        assert!(expiry["status"]["performanceOverride"].is_null());
        release.store(true, Ordering::Release);
        assert_eq!(request.await["result"]["status"], 2);
        let bytes = crate::service_frame::read_payload(&mut client)
            .await
            .unwrap();
        let committed: Value = serde_json::from_slice(&bytes).unwrap();
        assert!(committed["status"]["performanceOverride"].is_null());
        assert_eq!(committed["status"]["activePowerSource"], 1);
        assert_eq!(
            committed["status"]["desiredState"],
            expiry["status"]["desiredState"]
        );
        assert_eq!(
            committed["status"]["confirmedState"],
            observed["status"]["confirmedState"]
        );
        assert!(
            committed["status"]["revision"].as_u64().unwrap()
                > expiry["status"]["revision"].as_u64().unwrap()
        );
        assert_eq!(rig.reader.last_completed_for_tests(), timestamp);
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        emit_fixture_frame("peer_subscription", "afterStored", "statusEvent", &bytes);
        drop(client);
    };
    let (result, ()) = tokio::join!(server, exercise);
    assert!(result.is_ok());
    let foreign = rig.request_as(json!({"operation":"getStatus","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000099"}), USER, "S-1-5-21-111-222-333-1002").await;
    assert!(foreign["status"]["performanceOverride"].is_null());
    rig.stop().await;
}

// The aggregate carries the profile's own schema, so a frozen older managed
// profile is read as a committed older envelope, as managed migration reads it.
fn managed_application_files(bytes: &[u8]) -> MemoryFiles {
    let files = MemoryFiles::default();
    files.profiles(1);
    let schema = serde_json::from_slice::<Value>(bytes).unwrap()["schemaVersion"].clone();
    let mut preferences = files.document("battery-preferences.json");
    preferences["schemaVersion"] = schema.clone();
    files.put("battery-preferences.json", preferences);
    files
        .bytes
        .lock()
        .unwrap()
        .insert("profiles.json".into(), bytes.into());
    let all = files.bytes.lock().unwrap();
    let profile = crate::configuration::digest_hex(&all["profiles.json"]);
    let preferences = crate::configuration::digest_hex(&all["battery-preferences.json"]);
    drop(all);
    files.put(
        "battery-configuration.commit.json",
        json!({"schemaVersion":schema,"profileSha256":profile,"preferencesSha256":preferences}),
    );
    files
}
// Managed ProfileStore normalises schema-26 lighting on read; the managed
// BatteryConfigurationStoreTests consume the same cases.
#[test]
fn installed_storage_reads_schema26_lighting_as_managed_normalises_it() {
    let vector: Value = serde_json::from_str(include_str!(
        "../../../../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-schema26-lighting.json"
    ))
    .unwrap();
    for case in vector["cases"].as_array().unwrap() {
        let staged = MemoryFiles::default();
        staged.profiles(1);
        let mut profile = staged.document("profiles.json");
        profile["schemaVersion"] = case["schema"].clone();
        let mut lighting = profile["profiles"]["pluggedIn"]["lighting"].clone();
        for (name, value) in case["set"].as_object().unwrap() {
            lighting[name] = value.clone();
        }
        profile["profiles"]["pluggedIn"]["lighting"] = lighting.clone();
        profile["profiles"]["applicationProfiles"][0]["overrides"]["battery"] =
            json!({ "lighting": lighting });
        let files = managed_application_files(&serde_json::to_vec(&profile).unwrap());
        let storage = Storage::open(Box::new(files), SID).unwrap();
        let profiles = storage.profiles().unwrap().unwrap();
        let name = case["name"].as_str().unwrap();
        for lighting in [
            &profiles["pluggedIn"]["lighting"],
            &profiles["applicationProfiles"][0]["overrides"]["battery"]["lighting"],
        ] {
            // With service-profiles-v1 the store migrates the document when
            // it opens, and managed's writer omits a false keyboard effect
            // and a null independent control.
            let keyboard = lighting.get("controlsKeyboardEffect");
            assert_eq!(
                keyboard.unwrap_or(&json!(false)),
                &case["controlsKeyboardEffect"],
                "{name}"
            );
            assert_eq!(
                lighting["independentControls"]["brightness"],
                case["set"]["independentControls"]["brightness"],
                "{name}"
            );
        }
    }
}
/// Reports Game.exe and records the pair without applying it: unknown
/// Windows power admits no reconciliation (`reconcile_classes`), so the
/// explicit request that follows is measured alone. Without
/// `service-profile-triggers-v1` a report only stores its context.
async fn report_game(rig: &Rig) {
    // `Option::replace` stores `Some(None)`: Windows power unknown.
    let windows = rig
        .port
        .state
        .lock()
        .unwrap()
        .windows_on_battery
        .replace(None);
    let result = rig.request(json!({"operation":"reportApplicationContext","version":91,"requestId":"bbbbbbbb-bbbb-cccc-dddd-000000000003","instanceId":"cccccccc-bbbb-cccc-dddd-000000000003","sequence":1,"executablePath":r"C:\Games\Game.exe"})).await;
    rig.port.state.lock().unwrap().windows_on_battery = windows;
    let expected = if cfg!(feature = "service-profile-triggers-v1") {
        "The active application profile was recorded."
    } else {
        "Application context stored; automatic native reconciliation is not enabled."
    };
    assert_eq!(result["result"], json!({"status":0,"message":expected}));
}
#[tokio::test]
async fn edge_managed_fan_control_only_application_precedence_and_lease_are_composed() {
    // Current schema 27 documents and the frozen schema 25 migration inputs.
    for [fixed, curve, automatic] in [
        [
            include_bytes!("../../../../../tests/fixtures/app-fans-0-1.json").as_slice(),
            include_bytes!("../../../../../tests/fixtures/app-fans-0-2.json").as_slice(),
            include_bytes!("../../../../../tests/fixtures/app-fans-1-0.json").as_slice(),
        ],
        [
            include_bytes!("../../../../../tests/fixtures/app-fans-0-1-v25.json").as_slice(),
            include_bytes!("../../../../../tests/fixtures/app-fans-0-2-v25.json").as_slice(),
            include_bytes!("../../../../../tests/fixtures/app-fans-1-0-v25.json").as_slice(),
        ],
    ] {
        for (bytes, report, expired, persistent, expected) in [
            (fixed, true, false, false, 0),
            (curve, true, false, false, 0),
            (automatic, true, false, false, 1),
            (fixed, false, false, false, 1),
            (fixed, true, true, false, 1),
            (automatic, true, false, true, 0),
        ] {
            let files = managed_application_files(bytes);
            if persistent {
                files.bytes.lock().unwrap().insert(
                    "manual-overrides.json".into(),
                    manual_files()
                        .read("manual-overrides.json")
                        .unwrap()
                        .unwrap(),
                );
            }
            let rig = Rig::new(files);
            let before = fresh_observe(&rig).await;
            assert_eq!(before["status"]["confirmedState"]["performance"], 5);
            assert!(before["status"]["confirmedState"]["customPerformance"].is_null());
            if report {
                report_game(&rig).await;
            }
            if expired {
                tokio::time::pause();
                tokio::time::advance(std::time::Duration::from_secs(16)).await;
                tokio::time::resume();
            }
            let result = rig.set(91, value(1, 1)).await;
            assert_eq!(
                result["result"]["status"], expected,
                "report={report} expired={expired} persistent={persistent}"
            );
            assert_eq!(
                rig.port.state.lock().unwrap().writes,
                if expected == 1 { 2 } else { 0 }
            );
            if expected == 0 {
                assert!(
                    rig.files
                        .read("thermal-mutation.journal")
                        .unwrap()
                        .is_none()
                );
            }
            rig.stop().await;
        }
        // Explicit coherent Fans remains authoritative; it does not fall through to base Automatic.
        let files = managed_application_files(fixed);
        let mut profile = files.document("profiles.json");
        profile["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"]["fans"] = json!(1);
        let files = managed_application_files(&serde_json::to_vec(&profile).unwrap());
        let rig = Rig::new(files);
        report_game(&rig).await;
        assert_eq!(rig.set(91, value(1, 1)).await["result"]["status"], 0);
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        rig.stop().await;
        // Custom Automatic has real supported CPU Medium/GPU High observations and
        // takes the direct pair even when saved application fan intent is manual.
        let rig = Rig::new(managed_application_files(fixed));
        {
            let mut port = rig.port.state.lock().unwrap();
            port.fans = [0, 0];
            port.custom_levels = [1, 2];
        }
        report_game(&rig).await;
        let observed = fresh_observe(&rig).await;
        assert_eq!(observed["success"], true);
        assert_eq!(
            observed["status"]["confirmedState"]["customPerformance"]["cpu"],
            1
        );
        assert_eq!(rig.set(91, value(1, 1)).await["result"]["status"], 1);
        {
            let port = rig.port.state.lock().unwrap();
            assert_eq!(port.writes, 2);
            assert_eq!(
                port.sent
                    .iter()
                    .filter(|(class, command, _, _)| *class == 13 && *command == 2)
                    .count(),
                2
            );
        }
        rig.stop().await;
    }
    // Persistent FanControl without Fans is a different, invalid document shape.
    let files = manual_files();
    let mut invalid = files.document("manual-overrides.json");
    invalid["overrides"]["fans"] = Value::Null;
    files.put("manual-overrides.json", invalid);
    let before = files.read("manual-overrides.json").unwrap();
    assert!(Storage::open(Box::new(files.clone()), SID).is_err());
    assert_eq!(files.read("manual-overrides.json").unwrap(), before);
}
#[tokio::test]
async fn edge_managed_saved_custom_survives_unpublished_activation_and_private_frames() {
    let managed: Value = serde_json::from_slice(include_bytes!(
        "../../../../../tests/fixtures/custom-gpu-maximum.json"
    ))
    .unwrap();
    let gpu = managed["overrides"]["gpuClockTuning"].clone();
    for key in ["effectiveOffsets", "offsets"] {
        let files = MemoryFiles::default();
        let mut invalid = managed.clone();
        invalid["overrides"]["gpuClockTuning"][key]["coreMegahertz"] = json!(10001);
        files.put("manual-overrides.json", invalid);
        assert!(Storage::open(Box::new(files), SID).is_err());
    }
    let mut custom_request = value(5, 1);
    custom_request["customPerformance"] = managed["overrides"]["customPerformance"].clone();
    let custom: crate::service_status::PerformanceOverride =
        serde_json::from_value(custom_request.clone()).unwrap();
    // The request is well formed; maximum fans are outside the RZ09-0581
    // Custom range, so admission refuses it before any write or journal row.
    assert!(custom.validate().is_ok());
    {
        let rig = Rig::new(MemoryFiles::default());
        let reply = rig.set(91, custom_request).await;
        assert_eq!(reply["result"]["status"], 2);
        assert!(
            reply["result"]["message"]
                .as_str()
                .unwrap()
                .ends_with("No supported performance destination is selected by the current power and profile policy.")
        );
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        rig.stop().await;
    }
    for enabled in [false, true] {
        let rig = Rig::new(MemoryFiles::default());
        let mut request = value(1, 1);
        request["gpuClockTuning"] = gpu.clone();
        request["gpuClockTuning"]["enabled"] = json!(enabled);
        if !enabled {
            request["gpuClockTuning"]["effectiveOffsets"] =
                json!({"coreMegahertz":0,"memoryMegahertz":0});
        }
        assert_eq!(rig.set(91, request).await["result"]["status"], 2);
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        rig.stop().await;
    }

    for bytes in [
        include_bytes!("../../../../../tests/fixtures/custom-basic.json").as_slice(),
        include_bytes!("../../../../../tests/fixtures/custom-gpu-maximum.json").as_slice(),
        // Frozen schema 8 documents are read without a rewrite, as managed migration.
        include_bytes!("../../../../../tests/fixtures/custom-basic-v8.json").as_slice(),
        include_bytes!("../../../../../tests/fixtures/custom-gpu-maximum-v8.json").as_slice(),
    ] {
        let files = MemoryFiles::default();
        files
            .bytes
            .lock()
            .unwrap()
            .insert("manual-overrides.json".into(), bytes.into());
        let expected: Value = serde_json::from_slice(bytes).unwrap();
        let rig = Rig::new(files);
        rig.custody.activation_refusal.store(1, Ordering::Release);
        assert_eq!(rig.set(91, value(1, 1)).await["result"]["status"], 2);
        assert_eq!(
            rig.files.read("manual-overrides.json").unwrap().unwrap(),
            bytes
        );
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
        for operation in ["getStatus", "observeState", "subscribeStatus"] {
            let mut request = json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000078"});
            if operation == "observeState" {
                request["includeLiveReadings"] = json!(false);
                request["requestedLiveReadings"] = json!(0);
                request["effectiveLiveReadings"] = json!(0);
            }
            let own = rig.request(request.clone()).await;
            assert_eq!(own["success"], true);
            assert_eq!(own["status"]["performanceOverride"]["value"], 5);
            assert_eq!(
                own["status"]["performanceOverride"]["customPerformance"],
                expected["overrides"]["customPerformance"]
            );
            assert_eq!(
                own["status"]["performanceOverride"]["gpuClockTuning"],
                expected["overrides"]["gpuClockTuning"]
            );
            if operation != "getStatus" {
                assert_eq!(own["status"]["confirmedState"]["performance"], 5);
                assert!(own["status"]["confirmedState"]["customPerformance"].is_null());
            }
            let foreign = rig
                .request_as(request, USER, "S-1-5-21-111-222-333-1002")
                .await;
            assert!(foreign["status"]["performanceOverride"].is_null());
        }
        let (mut client, server) = duplex(65536);
        let source = rig.source.clone();
        let publications = source.lock().await.publications();
        let server = serve_connection(
            Box::new(SidStream {
                inner: test_stream(server, USER, Fault::None),
                sid: SID.into(),
            }),
            source,
            publications,
            Rc::new(Subscriptions::new(2)),
        );
        let exercise = async {
            crate::service_frame::write_payload(&mut client, &serde_json::to_vec(&json!({"operation":"subscribeStatus","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000090"})).unwrap()).await.unwrap();
            crate::service_frame::read_payload(&mut client)
                .await
                .unwrap();
            // A new metadata sample creates a material publication; identical
            // thermal observations intentionally coalesce on this stream.
            rig.metadata.store(1, Ordering::Release);
            assert_eq!(fresh_observe(&rig).await["success"], true);
            let raw = tokio::time::timeout(
                std::time::Duration::from_secs(2),
                crate::service_frame::read_payload(&mut client),
            )
            .await
            .unwrap()
            .unwrap();
            let frame: Value = serde_json::from_slice(&raw).unwrap();
            assert_eq!(
                frame["status"]["performanceOverride"]["customPerformance"],
                expected["overrides"]["customPerformance"]
            );
            assert!(frame["status"]["confirmedState"]["customPerformance"].is_null());
            emit_fixture_frame("peer_subscription", "statusEvent", "statusEvent", &raw);
            drop(client);
        };
        let (result, ()) = tokio::join!(server, exercise);
        assert!(result.is_ok());
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert_eq!(
            rig.files.read("manual-overrides.json").unwrap().unwrap(),
            bytes
        );
        rig.stop().await;
    }
}
#[tokio::test]
async fn edge_shutdown_publishes_actual_retirement_after_one_failed_settlement() {
    let rig = Rig::new(MemoryFiles::default());
    rig.get().await;
    let reached = Arc::new(AtomicBool::new(false));
    let release = Arc::new(AtomicBool::new(false));
    let flag = reached.clone();
    let gate = release.clone();
    let files = rig.files.clone();
    {
        let mut port = rig.port.state.lock().unwrap();
        port.change_power = Some((2, 0));
        port.on_mutation_retired = Some(Arc::new(move || {
            files.fail_terminal.store(true, Ordering::Release);
            flag.store(true, Ordering::Release);
            while !gate.load(Ordering::Acquire) {
                std::thread::yield_now();
            }
        }));
    }
    {
        let request = rig.set(91, value(1, 1));
        tokio::pin!(request);
        let held = async {
            while !reached.load(Ordering::Acquire) {
                tokio::task::yield_now().await;
            }
        };
        tokio::select! { _ = &mut request => panic!("request completed before actual retirement release"), r = tokio::time::timeout(HANG_GUARD, held) => r.unwrap() }
    }
    release.store(true, Ordering::Release);
    assert!(rig.reader.stop().await.is_err());
    assert!(rig.custody.retired.load(Ordering::Acquire));
    assert!(rig.reader.confirm_drained().is_ok());
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 1);
    let pending = rig.files.read("thermal-mutation.journal.pending").unwrap();
    assert!(pending.is_some());
    rig.reader.stop().await.unwrap();
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 1);
    assert_eq!(
        rig.files.read("thermal-mutation.journal.pending").unwrap(),
        pending
    );
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
}

#[tokio::test]
async fn edge_outer_terminal_fatal_survives_healthy_fresh_recheck() {
    let rig = Rig::new(MemoryFiles::default());
    let retired = Arc::new(AtomicBool::new(false));
    let flag = retired.clone();
    let files = rig.files.clone();
    let original = Arc::new(StdMutex::new(None));
    let captured = original.clone();
    {
        let mut port = rig.port.state.lock().unwrap();
        port.change_power = Some((2, 0));
        port.on_mutation_retired = Some(Arc::new(move || {
            let prepared = files.read("thermal-mutation.journal").unwrap().unwrap();
            assert_eq!(
                serde_json::from_slice::<Value>(prepared.strip_suffix(b"\n").unwrap()).unwrap()["progress"],
                0
            );
            *captured.lock().unwrap() = Some(prepared);
            files.fatal_terminal.store(true, Ordering::Release);
            flag.store(true, Ordering::Release);
        }));
    }
    let events: Events = Rc::default();
    let controls = Controls::new();
    let (mut ports, tx) = ports(Fault::None, &events);
    ports.source = rig.source.clone();
    let (running, ready) = oneshot::channel();
    let service = lifecycle(&controls, Ok(ports), &events, move || {
        running.send(()).unwrap();
    });
    let exercise = async {
        ready.await.unwrap();
        let (mut client, server) = duplex(65536);
        tx.send(Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }))
        .unwrap();
        crate::service_frame::write_payload(
            &mut client,
            &serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":91,
                "requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000099","value":value(1,1)}))
            .unwrap(),
        )
        .await
        .unwrap();
        assert!(
            crate::service_frame::read_payload(&mut client)
                .await
                .is_err(),
            "terminal FatalAdmission must not become an ordinary Blocked reply"
        );
    };
    let (result, ()) = tokio::join!(service, exercise);
    assert!(result.is_err());
    assert!(events.borrow().contains(&"failed"));
    assert!(retired.load(Ordering::Acquire));
    Fresh::verify_health(rig.custody.as_ref()).unwrap();
    assert!(!rig.fatal_capture.load(Ordering::Acquire));
    assert!(!rig.custody.fatal_health.load(Ordering::Acquire));
    assert!(!rig.custody.integrity_lost.load(Ordering::Acquire));
    assert_eq!(rig.custody.activations.load(Ordering::Acquire), 1);
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 1);
    assert_eq!(
        rig.files.read("thermal-mutation.journal").unwrap(),
        *original.lock().unwrap()
    );
    assert!(
        rig.files
            .read("thermal-mutation.journal.pending")
            .unwrap()
            .is_none()
    );
    assert!(rig.custody.retired.load(Ordering::Acquire));
    let port = rig.port.state.lock().unwrap();
    assert_eq!(port.writes, 0);
    assert_eq!(port.opened, port.retired);
}

#[tokio::test]
async fn edge_outer_first_intent_and_worker_fatals_preserve_class_and_fences() {
    for worker_error in [false, true] {
        let rig = Rig::new(MemoryFiles::default());
        let original = Arc::new(StdMutex::new(None));
        let captured = original.clone();
        let files = rig.files.clone();
        rig.files
            .fatal_first_intent
            .store(!worker_error, Ordering::Release);
        {
            let mut port = rig.port.state.lock().unwrap();
            if worker_error {
                port.change_power = Some((2, 0));
                port.mutation_worker_error = Some(Arc::new(|| {
                    crate::installed::readiness::thermal_mutation::fatal_admission(
                        "injected original worker fatal",
                    )
                }));
            }
            port.on_mutation_retired = Some(Arc::new(move || {
                let prepared = files.read("thermal-mutation.journal").unwrap().unwrap();
                assert_eq!(
                    serde_json::from_slice::<Value>(prepared.strip_suffix(b"\n").unwrap()).unwrap()
                        ["progress"],
                    0
                );
                *captured.lock().unwrap() = Some(prepared);
            }));
        }
        let events: Events = Rc::default();
        let controls = Controls::new();
        let (mut ports, tx) = ports(Fault::None, &events);
        ports.source = rig.source.clone();
        let (running, ready) = oneshot::channel();
        let service = lifecycle(&controls, Ok(ports), &events, move || {
            running.send(()).unwrap();
        });
        let exercise = async {
            ready.await.unwrap();
            let (mut client, server) = duplex(65536);
            tx.send(Box::new(SidStream {
                inner: test_stream(server, USER, Fault::None),
                sid: SID.into(),
            }))
            .unwrap();
            crate::service_frame::write_payload(
                &mut client,
                &serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":91,
                "requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000099","value":value(1,1)}))
                .unwrap(),
            )
            .await
            .unwrap();
            assert!(
                crate::service_frame::read_payload(&mut client)
                    .await
                    .is_err(),
                "typed fatal became ordinary reply"
            );
        };
        let (result, ()) = tokio::join!(service, exercise);
        assert!(result.is_err());
        assert!(events.borrow().contains(&"failed"));
        Fresh::verify_health(rig.custody.as_ref()).unwrap();
        assert!(!rig.fatal_capture.load(Ordering::Acquire));
        assert_eq!(
            rig.files.first_intent_attempts.load(Ordering::Acquire),
            usize::from(!worker_error)
        );
        assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
        let prepared = rig.files.read("thermal-mutation.journal").unwrap();
        assert_eq!(prepared, *original.lock().unwrap());
        if !worker_error {
            assert_eq!(prepared, *rig.files.first_intent_prior.lock().unwrap());
        }
        assert!(
            rig.files
                .read("thermal-mutation.journal.pending")
                .unwrap()
                .is_none()
        );
        assert_eq!(
            rig.custody.retired.load(Ordering::Acquire),
            !worker_error,
            "an unconfirmed worker must never publish custody retirement"
        );
        let port = rig.port.state.lock().unwrap();
        assert_eq!(port.writes, 0);
        assert_eq!(port.opened, port.retired);
    }
}

#[tokio::test]
async fn edge_canceled_first_intent_fatal_is_sticky_for_get_observe_and_new_sid() {
    let rig = Rig::new(MemoryFiles::default());
    rig.files.fatal_first_intent.store(true, Ordering::Release);
    let gate = RetirementGate::install(&rig);
    {
        let request = rig.set(91, value(1, 1));
        tokio::pin!(request);
        tokio::select! { _ = &mut request => panic!("fatal request completed before controlled retirement release"), () = gate.reached() => {} }
    }
    Fresh::verify_health(rig.custody.as_ref()).unwrap();
    assert_eq!(rig.files.first_intent_attempts.load(Ordering::Acquire), 1);
    let prepared = rig.files.read("thermal-mutation.journal").unwrap();
    gate.open();
    for (operation, sid) in [
        ("getStatus", SID),
        ("observeState", SID),
        ("setPerformanceOverride", "S-1-5-21-111-222-333-1002"),
    ] {
        let request = if operation == "setPerformanceOverride" {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})
        } else if operation == "observeState" {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000097","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
        } else {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096"})
        };
        let (mut client, server) = duplex(65536);
        let publications = rig.source.lock().await.publications();
        let server = serve_connection(
            Box::new(SidStream {
                inner: test_stream(server, USER, Fault::None),
                sid: sid.into(),
            }),
            rig.source.clone(),
            publications,
            Rc::new(Subscriptions::new(2)),
        );
        let client = async {
            crate::service_frame::write_payload(
                &mut client,
                &serde_json::to_vec(&request).unwrap(),
            )
            .await
            .unwrap();
            crate::service_frame::read_payload(&mut client).await
        };
        let (server, reply) = tokio::join!(server, client);
        assert!(
            matches!(server, Err(ExchangeError::Runtime(error))
            if crate::installed::readiness::thermal_mutation::is_fatal_admission(&error)
                && error.to_string() == "injected FirstIntent staging protection failure"),
            "{operation} must propagate the same retained typed fatal"
        );
        assert!(
            reply.is_err(),
            "{operation} must not emit ordinary response"
        );
    }
    let opened = rig.port.state.lock().unwrap().opened;
    let stopped = rig.reader.stop().await;
    assert!(
        stopped.is_ok()
            || stopped
                .as_ref()
                .is_err_and(crate::installed::readiness::thermal_mutation::is_fatal_admission)
    );
    assert_eq!(rig.port.state.lock().unwrap().opened, opened);
    assert_eq!(rig.files.first_intent_attempts.load(Ordering::Acquire), 1);
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
    assert_eq!(
        rig.files.read("thermal-mutation.journal").unwrap(),
        prepared
    );
    assert_eq!(prepared, *rig.files.first_intent_prior.lock().unwrap());
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
}

#[tokio::test]
async fn correction_idle_owner_loss_precedes_direct_observe_and_retains_first_fatal() {
    let rig = Rig::new(MemoryFiles::default());
    let files = rig.files.clone();
    {
        let mut port = rig.port.state.lock().unwrap();
        port.change_power = Some((2, 0));
        port.on_mutation_retired = Some(Arc::new(move || {
            files.fail_terminal.store(true, Ordering::Release)
        }));
    }
    assert_eq!(rig.set(91, value(1, 1)).await["result"]["status"], 2);
    assert!(rig.reader.idle_for_tests());
    let prepared = rig.files.read("thermal-mutation.journal").unwrap();
    let pending = rig.files.read("thermal-mutation.journal.pending").unwrap();
    assert!(prepared.is_some() && pending.is_some());
    let (opened, queries) = {
        let port = rig.port.state.lock().unwrap();
        (port.opened, port.sent.clone())
    };
    rig.custody.integrity_lost.store(true, Ordering::Release);
    // No GetStatus after loss: this is the direct outer ObserveState route.
    for operation in [
        "observeState",
        "getStatus",
        "observeState",
        "setPerformanceOverride",
    ] {
        let request = if operation == "setPerformanceOverride" {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})
        } else if operation == "observeState" {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000097","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
        } else {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096"})
        };
        let (mut client, server) = duplex(65536);
        let publications = rig.source.lock().await.publications();
        let server = serve_connection(
            Box::new(SidStream {
                inner: test_stream(server, USER, Fault::None),
                sid: SID.into(),
            }),
            rig.source.clone(),
            publications,
            Rc::new(Subscriptions::new(2)),
        );
        let client = async {
            crate::service_frame::write_payload(
                &mut client,
                &serde_json::to_vec(&request).unwrap(),
            )
            .await
            .unwrap();
            crate::service_frame::read_payload(&mut client).await
        };
        let (server, reply) = tokio::join!(server, client);
        assert!(
            matches!(server, Err(ExchangeError::Runtime(error)) if crate::installed::readiness::thermal_mutation::is_fatal_admission(&error) && error.to_string() == "injected original owner integrity lost")
        );
        assert!(reply.is_err());
        // Restoring the underlying fixture cannot erase the first typed fatal.
        rig.custody.integrity_lost.store(false, Ordering::Release);
    }
    assert_eq!(rig.port.state.lock().unwrap().opened, opened);
    assert_eq!(rig.port.state.lock().unwrap().sent, queries);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    assert_eq!(
        rig.files.read("thermal-mutation.journal").unwrap(),
        prepared
    );
    assert_eq!(
        rig.files.read("thermal-mutation.journal.pending").unwrap(),
        pending
    );
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 1);
    rig.reader.stop().await.unwrap();
}

#[tokio::test]
async fn correction_inflight_config_drift_distinguishes_stored_from_first_intent() {
    for (prior, submitted) in [
        (None, false),
        (Some(5), false),
        (Some(6), false),
        (None, true),
    ] {
        let rig = Rig::new(MemoryFiles::default());
        if let Some(progress) = prior {
            if progress == 6 {
                rig.port.state.lock().unwrap().change_power = Some((2, 0));
            }
            let seeded = rig.set(90, value(1, 1)).await;
            assert_eq!(
                seeded["result"]["status"],
                if progress == 5 { 1 } else { 2 }
            );
            assert_eq!(
                Storage::open(Box::new(rig.files.clone()), SID)
                    .unwrap()
                    .progress(),
                Some(progress)
            );
            let mut port = rig.port.state.lock().unwrap();
            assert_eq!(port.opened, port.retired);
            port.change_power = None;
            // The seed ends on battery, where managed rejects the AC override
            // below before storing it; the drift request runs on full power.
            port.actual = 15;
        }
        let prior_journal = rig.files.read("thermal-mutation.journal").unwrap();
        let prior_writes = rig.port.state.lock().unwrap().writes;
        let before = fresh_observe(&rig).await;
        let timestamp = rig.reader.last_completed_for_tests();
        let retired = Arc::new(AtomicBool::new(false));
        let release = Arc::new(AtomicBool::new(false));
        struct Release(Arc<AtomicBool>);
        impl Drop for Release {
            fn drop(&mut self) {
                self.0.store(true, Ordering::Release);
            }
        }
        let _release_on_failure = Release(release.clone());
        let flag = retired.clone();
        let gate = release.clone();
        {
            let mut port = rig.port.state.lock().unwrap();
            if submitted {
                port.fail_write = Some(1);
            }
            port.on_mutation_retired = Some(Arc::new(move || {
                flag.store(true, Ordering::Release);
                while !gate.load(Ordering::Acquire) {
                    std::thread::yield_now();
                }
            }));
        }
        let mut desired = value(2, 1);
        if !submitted {
            desired["gpuClockTuning"] =
                json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
        }
        let request = rig.set(91, desired);
        tokio::pin!(request);
        let held = async {
            while !retired.load(Ordering::Acquire) {
                tokio::task::yield_now().await;
            }
        };
        tokio::select! { _ = &mut request => panic!("retirement must remain held"), result = tokio::time::timeout(HANG_GUARD, held) => result.unwrap() }
        assert_eq!(
            rig.files.document("manual-overrides.json")["overrides"]["performance"],
            2
        );
        let mut foreign = rig.files.document("manual-overrides.json");
        foreign["overrides"]["performance"] = json!(1);
        rig.files.put("manual-overrides.json", foreign);
        let foreign = rig.files.read("manual-overrides.json").unwrap();
        let drift = rig.get().await;
        assert!(drift["status"]["performanceOverride"].is_null());
        assert_eq!(drift["status"]["writesBlocked"], true);
        assert_eq!(rig.reader.last_completed_for_tests(), timestamp);
        release.store(true, Ordering::Release);
        let result = request.await;
        assert_eq!(result["result"]["status"], 2);
        let message = result["result"]["message"].as_str().unwrap();
        if submitted {
            assert!(message.contains("may have been submitted"));
            let storage = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
            assert_eq!(storage.progress(), Some(1));
            assert!(
                rig.set(92, value(1, 1)).await["result"]["message"]
                    .as_str()
                    .unwrap()
                    .contains("may have been submitted")
            );
        } else {
            assert!(message.contains("configuration changed"));
            assert!(
                !message.contains("may have been submitted")
                    && !message.contains("earlier thermal submission")
            );
            assert_eq!(
                rig.files.read("thermal-mutation.journal").unwrap(),
                prior_journal
            );
            let later = rig.set(92, value(1, 1)).await;
            assert_eq!(later["result"]["status"], 2);
            assert!(
                later["result"]["message"]
                    .as_str()
                    .unwrap()
                    .contains("configuration changed")
            );
            assert_eq!(
                rig.files.read("thermal-mutation.journal").unwrap(),
                prior_journal
            );
        }
        assert_eq!(
            rig.port.state.lock().unwrap().writes,
            prior_writes + usize::from(submitted)
        );
        assert_eq!(rig.files.read("manual-overrides.json").unwrap(), foreign);
        if !submitted {
            // Expected private-policy faults leave independent fresh public reads valid.
            let observed = fresh_observe(&rig).await;
            assert_eq!(observed["success"], true);
            assert_eq!(
                observed["status"]["confirmedState"],
                before["status"]["confirmedState"]
            );
            assert!(observed["status"]["performanceOverride"].is_null());
            let witness = rig.observe(Duration::from_secs(2)).await.unwrap().unwrap();
            assert!(witness.has_fresh_mailbox_read());
        }
        rig.stop().await;
    }
}

#[tokio::test]
async fn correction_present_malformed_app_fans_refuse_committed_dispatch_without_writes() {
    let original = include_bytes!("../../../../../tests/fixtures/app-fans-0-1.json");
    for case in 0..14 {
        let rig = Rig::new(managed_application_files(original));
        let mut profile: Value = serde_json::from_slice(original).unwrap();
        let policy = &mut profile["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"];
        match case {
            0 => policy["fanControl"] = json!({}),
            1 => {
                policy["fanControl"].as_object_mut().unwrap().remove("mode");
            }
            2 => policy["fanControl"]["mode"] = Value::Null,
            3 => policy["fanControl"]["mode"] = json!({}),
            4 => policy["fanControl"]["mode"] = json!("Fixed"),
            5 => policy["fanControl"]["mode"] = json!(99),
            6 => policy["fanControl"]["mode"] = json!(-1),
            7 => {
                policy["fanControl"]
                    .as_object_mut()
                    .unwrap()
                    .remove("fixedSpeedRpm");
            }
            8 => policy["fanControl"]["manualCurve"] = Value::Null,
            9 => policy["fanControl"]["fixedSpeedRpm"] = json!(2001),
            10 => policy["fanControl"]["manualCurve"]["cpu"] = json!([]),
            11 => policy["fans"] = json!({}),
            12 => policy["fans"] = json!(99),
            13 => policy["fans"] = json!(0),
            _ => unreachable!(),
        }
        let changed = managed_application_files(&serde_json::to_vec(&profile).unwrap());
        *rig.files.bytes.lock().unwrap() = changed.bytes.lock().unwrap().clone();
        let retained = rig.files.bytes.lock().unwrap().clone();
        assert!(
            Storage::open(Box::new(rig.files.clone()), SID).is_err(),
            "case={case}"
        );
        if cfg!(feature = "service-profile-triggers-v1") {
            // The report selects from the saved profiles, so a store that
            // cannot open records the sequence but selects nothing, and says why.
            let report = rig.request(json!({"operation":"reportApplicationContext","version":91,"requestId":"bbbbbbbb-bbbb-cccc-dddd-000000000003","instanceId":"cccccccc-bbbb-cccc-dddd-000000000003","sequence":1,"executablePath":r"C:\Games\Game.exe"})).await;
            assert_eq!(report["result"]["status"], 2, "case={case}");
            assert!(
                report["result"]["message"]
                    .as_str()
                    .unwrap()
                    .starts_with("Saved configuration or the current session is unavailable: "),
                "case={case}"
            );
        } else {
            report_game(&rig).await;
        }
        // No paused clock advancement: the accepted report's actual 15s lease is live.
        let result = rig.set(91, value(1, 1)).await;
        assert_eq!(result["result"]["status"], 2, "case={case}");
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        assert!(
            rig.files
                .read("thermal-mutation.journal")
                .unwrap()
                .is_none()
        );
        assert_eq!(*rig.files.bytes.lock().unwrap(), retained);
        rig.stop().await;
    }
}

#[tokio::test]
async fn correction_absent_null_and_fans_only_app_policy_preserve_inheritance() {
    let original = include_bytes!("../../../../../tests/fixtures/app-fans-0-1.json");
    for case in 0..3 {
        let mut profile: Value = serde_json::from_slice(original).unwrap();
        let policy = &mut profile["profiles"]["applicationProfiles"][0]["overrides"]["pluggedIn"];
        // A configured performance override remains when both fan fields inherit.
        policy["performance"] = json!(1);
        policy["fanControl"] = Value::Null;
        if case == 0 {
            policy.as_object_mut().unwrap().remove("fanControl");
            policy.as_object_mut().unwrap().remove("fans");
        }
        if case == 2 {
            policy["fans"] = json!(1);
        }
        let rig = Rig::new(managed_application_files(
            &serde_json::to_vec(&profile).unwrap(),
        ));
        report_game(&rig).await;
        let result = rig.set(91, value(1, 1)).await;
        assert_eq!(result["result"]["status"], if case == 2 { 0 } else { 1 });
        assert_eq!(
            rig.port.state.lock().unwrap().writes,
            if case == 2 { 0 } else { 2 }
        );
        rig.stop().await;
    }
}

#[tokio::test]
async fn correction_disconnected_first_intent_fatal_precedes_later_owner_loss() {
    let rig = Rig::new(MemoryFiles::default());
    rig.files.fatal_first_intent.store(true, Ordering::Release);
    let gate = RetirementGate::install(&rig);
    let (mut client, server) = duplex(65536);
    let publications = rig.source.lock().await.publications();
    let server = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }),
        rig.source.clone(),
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let disconnect = async {
        crate::service_frame::write_payload(
            &mut client,
            &serde_json::to_vec(&json!({
                "operation":"setPerformanceOverride","version":91,
                "requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000091","value":value(1,1)
            }))
            .unwrap(),
        )
        .await
        .unwrap();
        gate.reached().await;
        // Exercise serve_mutation's real biased disconnect branch while the
        // accepted mutation future is still waiting for actual retirement.
        drop(client);
    };
    let (server, ()) = tokio::time::timeout(RetirementGate::LIVENESS, async {
        tokio::join!(server, disconnect)
    })
    .await
    .unwrap();
    assert!(matches!(server, Err(ExchangeError::Client(_))));
    rig.source.lock().await.verify_origin().unwrap();
    assert_eq!(rig.files.first_intent_attempts.load(Ordering::Acquire), 1);
    let prepared = rig.files.read("thermal-mutation.journal").unwrap();
    assert_eq!(prepared, *rig.files.first_intent_prior.lock().unwrap());
    // A was retained by the worker before the disconnected caller disappeared.
    // B must never replace A at an earlier reader-admission checkpoint.
    rig.custody.integrity_lost.store(true, Ordering::Release);
    gate.open();
    for (operation, sid) in [
        ("getStatus", SID),
        ("observeState", SID),
        ("setPerformanceOverride", "S-1-5-21-111-222-333-1002"),
    ] {
        let request = if operation == "setPerformanceOverride" {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})
        } else if operation == "observeState" {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000097","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
        } else {
            json!({"operation":operation,"version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096"})
        };
        let (mut client, server) = duplex(65536);
        let publications = rig.source.lock().await.publications();
        let server = serve_connection(
            Box::new(SidStream {
                inner: test_stream(server, USER, Fault::None),
                sid: sid.into(),
            }),
            rig.source.clone(),
            publications,
            Rc::new(Subscriptions::new(2)),
        );
        let client = async {
            crate::service_frame::write_payload(
                &mut client,
                &serde_json::to_vec(&request).unwrap(),
            )
            .await
            .unwrap();
            crate::service_frame::read_payload(&mut client).await
        };
        let (server, reply) = tokio::join!(server, client);
        assert!(matches!(server, Err(ExchangeError::Runtime(error))
            if crate::installed::readiness::thermal_mutation::is_fatal_admission(&error)
                && error.to_string() == "injected FirstIntent staging protection failure"));
        assert!(reply.is_err());
    }
    let stopped = rig.reader.stop().await;
    assert!(
        stopped.is_ok()
            || stopped
                .as_ref()
                .is_err_and(crate::installed::readiness::thermal_mutation::is_fatal_admission)
    );
    assert_eq!(rig.files.first_intent_attempts.load(Ordering::Acquire), 1);
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
    assert_eq!(
        rig.files.read("thermal-mutation.journal").unwrap(),
        prepared
    );
    assert!(
        rig.files
            .read("thermal-mutation.journal.pending")
            .unwrap()
            .is_none()
    );
    let port = rig.port.state.lock().unwrap();
    assert_eq!(port.writes, 0);
    assert_eq!(port.opened, port.retired);
}

#[tokio::test]
async fn correction_first_prepared_io_failure_retains_truthful_cause_and_all_evidence() {
    for cut in 1..=4 {
        let files = MemoryFiles::default();
        files.fail_prepared.store(cut, Ordering::Release);
        let rig = Rig::new(files);
        let retired = Arc::new(AtomicBool::new(false));
        let flag = retired.clone();
        rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
            flag.store(true, Ordering::Release);
        }));
        let result = rig.set(91, value(1, 1)).await;
        assert!(retired.load(Ordering::Acquire));
        assert_eq!(result["result"]["status"], 2);
        let message = result["result"]["message"].as_str().unwrap();
        assert!(message.contains("journal preparation could not be confirmed"));
        assert!(
            !message.contains("earlier thermal submission")
                && !message.contains("may have been submitted")
        );
        assert_eq!(
            rig.files.document("manual-overrides.json")["overrides"]["performance"],
            1
        );
        let prepared = rig.files.prepared_bytes.lock().unwrap().clone().unwrap();
        let row: Value = serde_json::from_slice(prepared.strip_suffix(b"\n").unwrap()).unwrap();
        assert_eq!(row["progress"], 0);
        assert_eq!(row["request"], "aaaaaaaa-bbbb-cccc-dddd-000000000091");
        assert_eq!(
            rig.files.read("thermal-mutation.journal").unwrap(),
            (cut == 4).then(|| prepared.clone())
        );
        let pending = match cut {
            2 => Some(prepared[..prepared.len() / 2].to_vec()),
            3 => Some(prepared),
            _ => None,
        };
        assert_eq!(
            rig.files.read("thermal-mutation.journal.pending").unwrap(),
            pending
        );
        let retained = rig.files.bytes.lock().unwrap().clone();
        // Public reads may recover independently; journal persistence is never retried.
        let observed = fresh_observe(&rig).await;
        assert_eq!(observed["success"], true);
        assert_eq!(observed["status"]["writesBlocked"], true);
        assert!(
            !observed["status"]["message"]
                .as_str()
                .unwrap()
                .contains("earlier thermal submission")
        );
        let later = rig.set(92, value(2, 1)).await;
        assert_eq!(later["result"]["status"], 2);
        assert_eq!(later["result"]["message"], result["result"]["message"]);
        assert_eq!(*rig.files.bytes.lock().unwrap(), retained);
        assert_eq!(rig.files.prepared_attempts.load(Ordering::Acquire), 1);
        assert_eq!(
            rig.files.submission_intent_attempts.load(Ordering::Acquire),
            0
        );
        assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        rig.stop().await;
        assert_eq!(*rig.files.bytes.lock().unwrap(), retained);
        assert_eq!(rig.files.prepared_attempts.load(Ordering::Acquire), 1);
    }
}

#[tokio::test]
async fn correction_first_prepared_protection_failure_remains_fatal() {
    let files = MemoryFiles::default();
    files.fail_prepared.store(5, Ordering::Release);
    let rig = Rig::new(files);
    let (mut client, server) = duplex(65536);
    let publications = rig.source.lock().await.publications();
    let server = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }),
        rig.source.clone(),
        publications,
        Rc::new(Subscriptions::new(2)),
    );
    let client = async {
        crate::service_frame::write_payload(
            &mut client,
            &serde_json::to_vec(&json!({
                "operation":"setPerformanceOverride", "version":91,
                "requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000091", "value":value(1,1)
            }))
            .unwrap(),
        )
        .await
        .unwrap();
        assert!(
            crate::service_frame::read_payload(&mut client)
                .await
                .is_err()
        );
    };
    let (result, ()) = tokio::join!(server, client);
    let ExchangeError::Runtime(error) = result.unwrap_err() else {
        panic!("typed Prepared fatal became a client error");
    };
    assert!(crate::installed::readiness::thermal_mutation::is_fatal_admission(&error));
    assert!(
        error
            .to_string()
            .contains("injected Prepared protection failure")
    );
    let later = rig
        .source
        .lock()
        .await
        .snapshot(USER, Instant::now())
        .await
        .err()
        .unwrap();
    assert!(
        later
            .to_string()
            .contains("injected Prepared protection failure")
    );
    assert_eq!(rig.files.prepared_attempts.load(Ordering::Acquire), 1);
    assert_eq!(
        rig.files.submission_intent_attempts.load(Ordering::Acquire),
        0
    );
    assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
    assert!(
        rig.files
            .read("thermal-mutation.journal")
            .unwrap()
            .is_none()
    );
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    // A fatal fence is retained; only the actual local reader is drained.
    let _ = rig.reader.stop().await;
}

#[tokio::test]
async fn correction_first_intent_io_failure_retains_uncertainty_and_all_evidence() {
    for cut in 1..=3 {
        let files = MemoryFiles::default();
        files.fail_first_intent.store(cut, Ordering::Release);
        let rig = Rig::new(files);
        let retired = Arc::new(AtomicBool::new(false));
        let flag = retired.clone();
        rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
            flag.store(true, Ordering::Release);
        }));
        let result = rig.set(91, value(1, 1)).await;
        assert!(retired.load(Ordering::Acquire));
        assert_eq!(result["result"]["status"], 2);
        let message = result["result"]["message"].as_str().unwrap();
        assert!(message.contains("submission-intent journal publication could not be confirmed"));
        assert!(
            message.contains("Read-only reconciliation")
                && message.contains("no mutation will be replayed")
        );
        assert!(!message.contains("no mutation submission intent was reached"));
        let prepared = rig
            .files
            .first_intent_prior
            .lock()
            .unwrap()
            .clone()
            .unwrap();
        let attempted = rig
            .files
            .first_intent_bytes
            .lock()
            .unwrap()
            .clone()
            .unwrap();
        let rows: Vec<Value> = attempted
            .split(|b| *b == b'\n')
            .filter(|line| !line.is_empty())
            .map(|line| serde_json::from_slice(line).unwrap())
            .collect();
        assert_eq!(
            rows.iter()
                .map(|row| row["progress"].as_u64().unwrap())
                .collect::<Vec<_>>(),
            [0, 1]
        );
        assert!(
            rows.iter()
                .all(|row| row["request"] == "aaaaaaaa-bbbb-cccc-dddd-000000000091")
        );
        assert_eq!(
            rig.files.read("thermal-mutation.journal").unwrap(),
            Some(if cut == 3 {
                attempted.clone()
            } else {
                prepared.clone()
            })
        );
        let pending = match cut {
            1 => {
                Some(attempted[..prepared.len() + (attempted.len() - prepared.len()) / 2].to_vec())
            }
            2 => Some(attempted.clone()),
            _ => None,
        };
        assert_eq!(
            rig.files.read("thermal-mutation.journal.pending").unwrap(),
            pending
        );
        let retained = rig.files.bytes.lock().unwrap().clone();
        let status = rig.get().await;
        assert_eq!(status["status"]["writesBlocked"], true);
        let observed = fresh_observe(&rig).await;
        assert_eq!(observed["success"], true);
        assert_eq!(observed["status"]["writesBlocked"], true);
        let later = rig.set(92, value(2, 1)).await;
        assert_eq!(later["result"]["status"], 2);
        assert_eq!(later["result"]["message"], result["result"]["message"]);
        assert_eq!(*rig.files.bytes.lock().unwrap(), retained);
        let reopened = Storage::open(Box::new(rig.files.clone()), SID);
        if cut == 3 {
            let storage = reopened.unwrap();
            assert_eq!(storage.progress(), Some(1));
            assert!(storage.pending());
            assert!(
                storage
                    .pending_reason()
                    .unwrap()
                    .contains("may have been submitted")
            );
        } else {
            assert!(
                reopened.is_err(),
                "remaining pending file must refuse reopen"
            );
        }
        assert_eq!(rig.files.prepared_attempts.load(Ordering::Acquire), 1);
        assert_eq!(
            rig.files.submission_intent_attempts.load(Ordering::Acquire),
            1
        );
        assert_eq!(rig.files.first_intent_attempts.load(Ordering::Acquire), 1);
        assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
        assert_eq!(rig.port.state.lock().unwrap().writes, 0);
        rig.stop().await;
        assert_eq!(*rig.files.bytes.lock().unwrap(), retained);
        assert_eq!(
            rig.files.submission_intent_attempts.load(Ordering::Acquire),
            1
        );
        assert_eq!(rig.files.terminal_attempts.load(Ordering::Acquire), 0);
        let port = rig.port.state.lock().unwrap();
        assert_eq!(port.writes, 0);
        assert_eq!(port.opened, port.retired);
    }
}

#[tokio::test]
async fn history_dispatcher_capacity_refusal_precedes_prepared_and_all_transport_writes() {
    // A positively known capacity refusal leaves nothing to reconcile: the intent
    // stays saved and the reply is Blocked, as managed reports a saved intent whose
    // hardware apply did not happen. Uncertain reservation I/O still quarantines.
    for uncertain in [false, true] {
        let files = MemoryFiles::default();
        files.profiles(1);
        if uncertain {
            files.reserve_uncertain.store(true, Ordering::Release);
        } else {
            files.capacity_refuse.store(true, Ordering::Release);
        }
        let rig = Rig::new(files);
        let result = rig.set(99, value(2, 1)).await;
        let message = result["result"]["message"].as_str().unwrap();
        if uncertain {
            assert_eq!(result["result"]["status"], 2);
            assert!(message.contains("no submission intent or thermal setter"));
        } else {
            assert_eq!(result["result"]["status"], 2);
            assert!(
                message.starts_with("Performance override intent was stored. Hardware result: ")
            );
            assert!(message.contains("capacity refused before publication"));
            assert!(rig.files.read("manual-overrides.json").unwrap().is_some());
        }
        assert_capacity_refusal_reached_no_journal_or_setter(rig).await;
    }
}

async fn assert_capacity_refusal_reached_no_journal_or_setter(rig: Rig) {
    assert_eq!(rig.files.prepared_attempts.load(Ordering::Acquire), 0);
    assert_eq!(
        rig.files.submission_intent_attempts.load(Ordering::Acquire),
        0
    );
    assert!(
        rig.files
            .read("thermal-mutation.journal")
            .unwrap()
            .is_none()
    );
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    rig.stop().await;
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
}

// Fan v2 tests execute only the preinspected in-memory dispatcher/Reader closure.
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_request(n: u32, mode: Option<u8>, rpm: u16) -> Value {
    let value = mode.map(|m| json!({"value":m,"lifetime":1}));
    let settings = mode.map(|m| {
        let mut v = crate::installed::readiness::thermal_storage::legacy_fan(m);
        v["fixedSpeedRpm"] = json!(rpm);
        v
    });
    json!({"operation":"setFanOverride","version":91,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"value":value,"settings":settings})
}
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_rig(selector: u8, manual: u8) -> Rig {
    // The saved preset matches the observed selector on both external classes,
    // so a return to Automatic keeps it. Handback-preset tests use `handback_rig`.
    let preset = if selector == 2 { 2 } else { 1 };
    fan_rig_with(selector, manual, preset)
}
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_rig_with(selector: u8, manual: u8, preset: i32) -> Rig {
    let files = MemoryFiles::default();
    files.profiles_with(preset, Some(preset));
    fan_rig_files(selector, manual, files)
}
/// A rig over `files` whose custody admits fan requests.
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_rig_files(selector: u8, manual: u8, files: MemoryFiles) -> Rig {
    let rig = Rig::new(files);
    *rig.custody.origin_for_fan.lock().unwrap() = Some(rig.origin.clone());
    {
        let mut s = rig.port.state.lock().unwrap();
        s.selectors = [selector; 2];
        s.fans = [manual; 2];
    }
    rig
}
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_rows(r: &Rig) -> Vec<Value> {
    r.files
        .read("thermal-mutation.journal")
        .unwrap()
        .unwrap_or_default()
        .split(|b| *b == 10)
        .filter(|b| !b.is_empty())
        .map(|b| serde_json::from_slice(b).unwrap())
        .collect()
}
/// A write job whose port fails to open sent nothing and is not applied
/// (`windows::mutation_worker`). It leaves the mutation lane confirmed, so the
/// next request still writes and owned Stop still hands manual fans back to
/// Automatic. This is the shared lane: thermal, fan, battery and temporary
/// charge requests and every owned-Stop cleanup run through it.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_write_port_that_fails_to_open_leaves_the_lane_and_owned_stop_working() {
    let rig = fan_rig_with(0, 0, 2);
    rig.get().await;
    let owned = rig.request(fan_request(101, Some(1), 3700)).await;
    assert_eq!(owned["result"]["status"], 1, "{owned}");
    let writes = rig.port.state.lock().unwrap().writes;
    rig.port.state.lock().unwrap().mutation_open_fails = 1;
    let refused = rig.request(fan_request(102, Some(1), 4000)).await;
    assert_ne!(refused["result"]["status"], 1, "{refused}");
    assert_eq!(rig.port.state.lock().unwrap().mutation_open_fails, 0);
    assert_eq!(
        rig.port.state.lock().unwrap().writes,
        writes,
        "nothing was sent"
    );
    let applied = rig.request(fan_request(103, Some(1), 4000)).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    rig.stop().await;
    assert_eq!(
        rig.port.state.lock().unwrap().fans,
        [0, 0],
        "handed back to Automatic"
    );
}
#[cfg(feature = "service-fan-mutation-v2")]
#[test]
fn fan_v2_strict_managed_shapes_and_target_validation() {
    use crate::service_status::StatusRequest;
    for mode in [0, 1, 2, 3] {
        for rpm in [2000, 2100, 3700, 4000, 5400] {
            assert!(
                StatusRequest::decode(
                    &serde_json::to_vec(&fan_request(1, Some(mode), rpm)).unwrap()
                )
                .is_ok()
            );
        }
    }
    assert!(
        StatusRequest::decode(&serde_json::to_vec(&fan_request(1, None, 3700)).unwrap()).is_ok()
    );
    for rpm in [1999, 2001, 5500] {
        assert!(
            StatusRequest::decode(&serde_json::to_vec(&fan_request(1, Some(1), rpm)).unwrap())
                .is_err()
        );
    }
    let valid = fan_request(1, Some(1), 3700);
    for (key, value) in [
        ("version", json!(80)),
        ("version", json!(PROTOCOL_VERSION - 1)),
        ("requestId", json!("bad")),
        ("extra", json!(1)),
    ] {
        let mut v = valid.clone();
        v[key] = value;
        assert!(StatusRequest::decode(&serde_json::to_vec(&v).unwrap()).is_err());
    }
    for text in [
        r#"{"operation":"setFanOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000001"}"#,
        r#"{"operation":"setFanOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000001","value":{"value":1,"value":3,"lifetime":1}}"#,
    ] {
        assert!(StatusRequest::decode(text.as_bytes()).is_err());
    }
    let mut v = fan_request(1, None, 3700);
    v["settings"] = valid["settings"].clone();
    assert!(StatusRequest::decode(&serde_json::to_vec(&v).unwrap()).is_err());
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_actual_dispatch_recipes_ownership_stop_timing_and_private_intent() {
    for (selector, manual, mode, rpm, setters, remaining) in [
        (0, 0, 1, 3700, 4, 10),
        (2, 0, 3, 3700, 4, 12),
        (0, 1, 1, 4000, 2, 6),
        (2, 1, 0, 3700, 2, 6),
    ] {
        // A saved preset unlike the owner's selector: Stop must still keep the owner's.
        let r = fan_rig_with(selector, manual, if selector == 2 { 1 } else { 2 });
        r.get().await;
        let outcome = r.request(fan_request(101, Some(mode), rpm)).await;
        assert_eq!(outcome["result"]["status"], 1, "{outcome}");
        let before = r.files.read("manual-overrides.json").unwrap();
        let rows = fan_rows(&r);
        let last = rows.last().unwrap();
        assert_eq!(last["progress"], if mode == 0 { 50 } else { 30 });
        let recipe = &last["fan"];
        assert_eq!(recipe["selector"], selector);
        assert_eq!(recipe["intent"]["rpm"], if mode == 3 { 5400 } else { rpm });
        assert_eq!(recipe["intent"]["mode"], mode);
        let ids = recipe["ids"].as_array().unwrap();
        assert_eq!(ids.len() * 2, remaining);
        {
            let state = r.port.state.lock().unwrap();
            assert_eq!(state.writes, setters);
            let writes: Vec<_> = state
                .sent
                .iter()
                .zip(&state.sent_at)
                .filter(|((c, k, _, _), _)| *c == 13 && matches!(*k, 1 | 2))
                .collect();
            for pair in writes.windows(2) {
                let ((_, prior, p, _), at) = pair[0];
                let ((_, next, _, _), bt) = pair[1];
                let min = if *prior == 2 && *next == 1 {
                    210
                } else if *prior == 1 {
                    65
                } else {
                    10
                };
                assert!(
                    *bt - *at >= std::time::Duration::from_millis(min),
                    "{p:?} timing {min}"
                );
            }
            assert!(
                state
                    .sent
                    .iter()
                    .all(|(c, k, _, _)| !(*c == 13 && *k == 0x81))
            );
        }
        let public=r.request_as(json!({"operation":"getStatus","version":91,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000077"}),USER,"S-1-5-21-111-222-333-1002").await;
        assert!(public["status"]["fanOverride"].is_null());
        assert!(public["status"]["fanControlOverride"].is_null());
        r.origin.stop();
        r.stop().await;
        assert_eq!(
            r.files.read("manual-overrides.json").unwrap(),
            before,
            "Stop rewrote saved fan intent"
        );
        let s = r.port.state.lock().unwrap();
        assert_eq!(s.writes, setters + if mode == 0 { 0 } else { 2 });
        assert_eq!(s.fans, [0, 0]);
        assert_eq!(s.selectors, [selector; 2]);
        assert_eq!(s.opened, s.retired);
        drop(s);
        assert_eq!(fan_rows(&r).last().unwrap()["progress"], 50);
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_noop_observation_and_pending_modes_never_mint_owner() {
    for (selector, manual, mode) in [
        (0, 0, 0),
        (4, 0, 1),
        (5, 0, 1),
        (3, 0, 1),
        (7, 0, 1),
        (0, 1, 2),
    ] {
        let r = fan_rig(selector, manual);
        // Admitted Custom levels: High/High (the fixture default) has no
        // explicit baseline at all, so nothing would be accepted or saved.
        r.port.state.lock().unwrap().custom_levels = [1, 2];
        r.get().await;
        let out = r.request(fan_request(102, Some(mode), 3700)).await;
        assert_eq!(
            out["result"]["status"],
            if selector == 0 && manual == 0 && mode == 0 {
                1
            } else {
                2
            }
        );
        assert_eq!(r.port.state.lock().unwrap().writes, 0);
        assert_eq!(
            r.files.document("manual-overrides.json")["overrides"]["fans"],
            mode
        );
        assert!(fan_rows(&r).is_empty());
        r.origin.stop();
        r.stop().await;
        assert_eq!(r.port.state.lock().unwrap().writes, 0);
    }
    let r = fan_rig(0, 1);
    r.get().await;
    r.origin.stop();
    r.stop().await;
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_reserves_operation_and_stop_before_prepared_and_never_refills() {
    for (selector, manual, remaining) in [(0, 1, 6usize), (0, 0, 10), (2, 0, 12)] {
        for enough in [false, true] {
            let r = fan_rig(selector, manual);
            r.get().await;
            // GetStatus is metadata-only. Explicit fan preflight consumes four IDs.
            let required = 4 + remaining + 10;
            let available = required - if enough { 0 } else { 2 };
            r.port.reserve_for_tests(127 - available / 2);
            let out = r.request(fan_request(103, Some(1), 3700)).await;
            if enough {
                assert_eq!(out["result"]["status"], 1, "{out}");
                r.port.exhaust();
                r.origin.stop();
                r.stop().await;
                assert_eq!(fan_rows(&r).last().unwrap()["progress"], 50);
            } else {
                assert_eq!(r.port.state.lock().unwrap().writes, 0);
                assert!(fan_rows(&r).is_empty());
                r.origin.stop();
                r.stop().await;
            }
        }
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_each_ambiguous_setter_fences_all_families_without_replay() {
    for cut in 1..=4 {
        let r = fan_rig(2, 0);
        r.get().await;
        r.port.state.lock().unwrap().corrupt_ack = Some(cut);
        let out = r.request(fan_request(104, Some(1), 3700)).await;
        assert_eq!(out["result"]["status"], 2);
        assert!(
            out["result"]["message"]
                .as_str()
                .unwrap()
                .contains("unresolved")
        );
        assert_eq!(r.port.state.lock().unwrap().writes, cut);
        let before = r.files.read("thermal-mutation.journal").unwrap();
        let fan_blocked = r.request(fan_request(106, Some(0), 3700)).await;
        assert_eq!(fan_blocked["result"]["status"], 2);
        let blocked = r.set(105, value(1, 1)).await;
        assert_ne!(blocked["result"]["status"], 1);
        assert_eq!(r.port.state.lock().unwrap().writes, cut);
        r.origin.stop();
        assert!(r.source.lock().await.drain().await.is_err());
        assert!(r.custody.retired.load(Ordering::Acquire));
        assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), before);
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_owned_updates_direct_auto_and_cleanup_refusals_preserve_evidence() {
    let r = fan_rig(0, 0);
    r.get().await;
    r.request(fan_request(110, Some(1), 3700)).await;
    r.request(fan_request(111, Some(3), 3700)).await;
    assert_eq!(r.port.state.lock().unwrap().writes, 6);
    let out = r.request(fan_request(112, Some(0), 3700)).await;
    assert_eq!(out["result"]["status"], 1);
    assert_eq!(r.port.state.lock().unwrap().writes, 8);
    r.origin.stop();
    r.stop().await;
    assert_eq!(r.port.state.lock().unwrap().writes, 8);
    for reason in 0..4 {
        let r = fan_rig(2, 0);
        r.get().await;
        r.request(fan_request(113, Some(1), 3700)).await;
        let saved = r.files.read("manual-overrides.json").unwrap();
        match reason {
            0 => r.custody.fresh_refuse.store(true, Ordering::Release),
            1 => r.origin.lose_trust(),
            2 => r.custody.lifecycle.ingress(
                windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
                4,
            ),
            _ => r.port.state.lock().unwrap().actual = 0,
        };
        r.origin.stop();
        assert!(r.source.lock().await.drain().await.is_err());
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        assert_eq!(r.files.read("manual-overrides.json").unwrap(), saved);
        assert_eq!(fan_rows(&r).last().unwrap()["progress"], 30);
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_stop_refusal_is_one_shot_and_saved_nonfan_fields_survive() {
    let r = fan_rig(0, 0);
    r.get().await;
    let perf = r.set(120, value(2, 1)).await;
    assert_eq!(perf["result"]["status"], 1);
    let before = r.files.document("manual-overrides.json");
    r.request(fan_request(121, Some(1), 3700)).await;
    let saved = r.files.document("manual-overrides.json");
    assert_eq!(
        before["overrides"]["performance"],
        saved["overrides"]["performance"]
    );
    assert_eq!(
        before["overrides"]["gpuClockTuning"],
        saved["overrides"]["gpuClockTuning"]
    );
    let current = r.get().await;
    assert_eq!(current["status"]["desiredState"]["fans"]["value"], 1);
    assert_eq!(
        current["status"]["fanControlOverride"]["fixedSpeedRpm"],
        3700
    );
    r.port.state.lock().unwrap().actual = 0;
    r.origin.stop();
    assert!(r.source.lock().await.drain().await.is_err());
    let sent = r.port.state.lock().unwrap().sent.len();
    assert!(r.source.lock().await.drain().await.is_err());
    assert_eq!(r.port.state.lock().unwrap().sent.len(), sent);
}

/// With nobody signed in, so the owner's own console capture refuses,
/// owned Stop hands manual fans back under the Service's cleanup admission
/// (`CleanupGuard`), which fan mutation composes without the keyboard owner;
/// without it, only the settle-only permit runs, and manual fans stay.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_owned_stop_cleanup_without_a_console_user_hands_fans_back() {
    for composed in [true, false] {
        let r = fan_rig_with(0, 0, 2);
        r.custody.scoped_fresh.store(true, Ordering::Release);
        r.get().await;
        let outcome = r.request(fan_request(101, Some(1), 3700)).await;
        assert_eq!(outcome["result"]["status"], 1, "{outcome}");
        if composed {
            let custody = r.custody.clone();
            r.runtime.read_cleanup(move |passed, scope| {
                CleanupFresh::capture(custody.clone(), passed, scope)
            });
        }
        r.custody.current_console.store(false, Ordering::Release);
        r.origin.stop();
        let stopped = r.source.lock().await.drain().await;
        assert_eq!(stopped.is_ok(), composed, "{composed}");
        let fans = r.port.state.lock().unwrap().fans;
        assert_eq!(fans, if composed { [0, 0] } else { [1, 1] }, "{composed}");
    }
}

/// Owned Stop cleanup is paced before its 31: on a device too slow to finish
/// the recipe by the Stop deadline it writes no row and sends no setter, so
/// no 31/4x tail is left. Its job registered and its baseline reads used the
/// owner's reserved IDs (reviewer R8), so no later admission of the same
/// Stop runs the recipe again: no reserved ID is sent twice.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_owned_stop_cleanup_too_slow_for_its_recipe_writes_no_row() {
    let r = fan_rig(0, 0);
    r.get().await;
    let applied = r.request(fan_request(141, Some(1), 3700)).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    let rows = fan_rows(&r).len();
    r.origin.stop();
    r.port.state.lock().unwrap().latency = Some(Duration::from_millis(400));
    let sent = r.port.state.lock().unwrap().sent.len();
    assert!(r.source.lock().await.drain().await.is_err());
    let state = r.port.state.lock().unwrap();
    let fan = state.sent[sent..]
        .iter()
        .filter(|(class, ..)| *class != 0)
        .collect::<Vec<_>>();
    assert!(!fan.is_empty());
    assert!(
        fan.iter().all(|(_, command, ..)| *command & 0x80 != 0),
        "{fan:?}"
    );
    let mut ids = fan.iter().map(|sent| sent.3).collect::<Vec<_>>();
    ids.sort_unstable();
    let all = ids.len();
    ids.dedup();
    assert_eq!(ids.len(), all, "{fan:?}");
    assert_eq!(state.fans, [1, 1]);
    drop(state);
    assert_eq!(fan_rows(&r).len(), rows);
}

/// One 400 ms stall in the cleanup's first exchange is charged once, at its
/// own length, as `Pace` charges every recipe: the rows are charged at the
/// second longest exchange, so a handback that needs well under a second of
/// its deadline is admitted and completes. A device slow throughout is still
/// refused (`fan_v2_owned_stop_cleanup_too_slow_for_its_recipe_writes_no_row`).
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_owned_stop_cleanup_after_one_stall_hands_fans_back() {
    let r = fan_rig(0, 0);
    r.get().await;
    let applied = r.request(fan_request(142, Some(1), 3700)).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    r.origin.stop();
    let sent = r.port.state.lock().unwrap().sent.len();
    r.port.state.lock().unwrap().exchange_stall = vec![(sent + 1, Duration::from_millis(400))];
    assert!(r.source.lock().await.drain().await.is_ok());
    let state = r.port.state.lock().unwrap();
    assert_ne!(state.sent[sent].0, 0, "the stall is a fan exchange");
    assert_eq!(state.fans, [0, 0]);
    drop(state);
    let rows = fan_rows(&r);
    assert_eq!(rows.last().unwrap()["progress"], 50, "{rows:?}");
}

/// Owned Stop's fan handback is paced to what is left of the source drain
/// (`StatusSource::drain_by`), not only to its job's own 3 s. At 100 ms per
/// exchange (35 ms each way and the 30 ms settle) it needs about 1.5 s: with
/// 4 s of drain left it completes inside its job's 3 s, but with 1 s left it
/// writes no row and sends no setter, where the job's 3 s alone would have
/// let the drain's timeout cancel it after its 31. The job's 3 s are counted
/// from before its worker starts, so the 1.5 s leaves room for a slow host.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_owned_stop_cleanup_is_paced_to_the_drain() {
    for (window, handed_back) in [(1000, false), (4000, true)] {
        let r = fan_rig(0, 0);
        r.get().await;
        let applied = r.request(fan_request(143, Some(1), 3700)).await;
        assert_eq!(applied["result"]["status"], 1, "{applied}");
        let rows = fan_rows(&r).len();
        r.origin.stop();
        r.port.state.lock().unwrap().latency = Some(Duration::from_millis(35));
        let sent = r.port.state.lock().unwrap().sent.len();
        let deadline = Instant::now() + Duration::from_millis(window);
        let stopped = r.source.lock().await.drain_by(deadline).await;
        assert_eq!(stopped.is_ok(), handed_back, "{window}");
        let state = r.port.state.lock().unwrap();
        let setters = state.sent[sent..]
            .iter()
            .filter(|(class, command, ..)| *class == 13 && *command & 0x80 == 0)
            .count();
        assert_eq!(setters > 0, handed_back, "{window}");
        assert_eq!(state.fans, if handed_back { [0, 0] } else { [1, 1] });
        drop(state);
        let after = fan_rows(&r);
        if handed_back {
            assert_eq!(after.last().unwrap()["progress"], 50, "{after:?}");
        } else {
            assert_eq!(after.len(), rows, "{after:?}");
        }
    }
}

/// The fan handback gets the fans' share of the source drain's own bound
/// (`SOURCE_DRAIN_TIMEOUT`, 2 s without the keyboard owner): at 150 ms per
/// exchange the handback needs about 2.25 s, which the job's own 3 s would
/// admit, so a plain drain refuses it with no row and no setter.
#[cfg(all(
    feature = "service-fan-mutation-v2",
    not(feature = "service-keyboard-driver-mode-v1")
))]
#[tokio::test]
async fn fan_v2_owned_stop_cleanup_gets_the_drain_bound() {
    let r = fan_rig(0, 0);
    r.get().await;
    let applied = r.request(fan_request(144, Some(1), 3700)).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    let rows = fan_rows(&r).len();
    r.origin.stop();
    r.port.state.lock().unwrap().latency = Some(Duration::from_millis(60));
    assert!(r.source.lock().await.drain().await.is_err());
    assert_eq!(r.port.state.lock().unwrap().fans, [1, 1]);
    assert_eq!(fan_rows(&r).len(), rows);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_actual_read_stop_race_retains_only_expected_cleanup_custody() {
    for expected in [true, false] {
        let r = fan_rig(2, 0);
        r.get().await;
        r.request(fan_request(130, Some(1), 3700)).await;
        r.reader.expire_for_tests();
        let held = Arc::new(AtomicBool::new(false));
        let opened = r.port.state.lock().unwrap().opened;
        r.port.state.lock().unwrap().held_read = Some(held.clone());
        let observation = r
            .source
            .lock()
            .await
            .observe_performance(USER, Instant::now() + Duration::from_secs(3));
        let stopping = async {
            let end = Instant::now() + HANG_GUARD;
            while r.port.state.lock().unwrap().opened == opened {
                assert!(Instant::now() < end);
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
            if expected {
                r.origin.stop();
            } else {
                r.origin.lose_trust();
            }
            assert!(ReadCustody::authorize(&*r.custody).is_none());
            assert_eq!(
                ReadCustody::authorize_owned_stop(&*r.custody).is_some(),
                expected
            );
            // Keep the original native callback retained through the Stop request.
            assert_eq!(r.port.state.lock().unwrap().writes, 4);
            held.store(true, Ordering::Release);
            let drained = r.source.lock().await.drain().await;
            assert_eq!(drained.is_ok(), expected);
        };
        let (read, ()) = tokio::join!(observation, stopping);
        assert!(read.unwrap().is_none());
        let state = r.port.state.lock().unwrap();
        assert_eq!(state.writes, if expected { 6 } else { 4 });
        assert_eq!(state.opened, state.retired);
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_identical_refused_request_keeps_original_owner_and_reserved_stop_ids() {
    let r = fan_rig(2, 0);
    r.get().await;
    r.request(fan_request(140, Some(1), 3700)).await;
    let bytes = r.files.read("manual-overrides.json").unwrap();
    r.port.exhaust();
    let out = r.request(fan_request(141, Some(1), 3700)).await;
    assert_ne!(out["result"]["status"], 1);
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    r.origin.stop();
    r.stop().await;
    assert_eq!(r.port.state.lock().unwrap().writes, 6);
    assert_eq!(r.files.read("manual-overrides.json").unwrap(), bytes);
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 50);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_prepared_only_stop_keeps_original_owner_and_resumes_exact_release() {
    let r = fan_rig(2, 0);
    r.get().await;
    r.request(fan_request(150, Some(1), 3700)).await;
    let at = r.port.state.lock().unwrap().power_reads + 2;
    let origin = r.origin.clone();
    r.port.state.lock().unwrap().on_power = Some(Arc::new(move |n| {
        if n == at {
            origin.stop();
        }
    }));
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(151, Some(1), 4000)).unwrap(),
    )
    .unwrap();
    let future = r.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let _ = future.await.unwrap();
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 6);
    r.stop().await;
    assert_eq!(r.port.state.lock().unwrap().writes, 6);
    assert_eq!(
        fan_rows(&r).last().unwrap()["request"],
        "aaaaaaaa-bbbb-cccc-dddd-000000000150"
    );
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 50);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_held_target_callback_blocks_retirement_then_retains_ambiguity() {
    let r = fan_rig(0, 1);
    r.get().await;
    let held = Arc::new(AtomicBool::new(false));
    r.port.state.lock().unwrap().held_write = Some((1, held.clone()));
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(160, Some(1), 3700)).unwrap(),
    )
    .unwrap();
    let mut mutation = r.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let end = Instant::now() + HANG_GUARD;
    while r.port.state.lock().unwrap().writes == 0 {
        assert!(Instant::now() < end);
        tokio::select! {result=&mut mutation=>panic!("held target completed early: {}",result.is_ok()),_=tokio::time::sleep(Duration::from_millis(5))=>{}}
    }
    drop(mutation);
    r.origin.stop();
    let mut source = r.source.lock().await;
    let mut draining = source.drain();
    assert!(
        tokio::time::timeout(Duration::from_millis(20), &mut draining)
            .await
            .is_err()
    );
    assert!(!r.custody.retired.load(Ordering::Acquire));
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    held.store(true, Ordering::Release);
    assert!(
        tokio::time::timeout(HANG_GUARD, draining)
            .await
            .unwrap()
            .is_err()
    );
    assert!(r.custody.retired.load(Ordering::Acquire));
    let s = r.port.state.lock().unwrap();
    assert_eq!(s.opened, s.retired);
    assert_eq!(s.writes, 1);
    drop(s);
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 10);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_known_capacity_refusal_preserves_owner_but_uncertain_reservation_fences() {
    for uncertain in [false, true] {
        let r = fan_rig(2, 0);
        r.get().await;
        r.request(fan_request(170, Some(1), 3700)).await;
        let before = r.files.read("thermal-mutation.journal").unwrap();
        if uncertain {
            r.files.reserve_uncertain.store(true, Ordering::Release);
        } else {
            r.files.capacity_refuse.store(true, Ordering::Release);
        }
        let result = r.request(fan_request(171, Some(1), 4000)).await;
        assert_ne!(result["result"]["status"], 1);
        assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), before);
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        r.origin.stop();
        let drained = r.source.lock().await.drain().await;
        assert_eq!(drained.is_err(), uncertain);
        assert_eq!(
            r.port.state.lock().unwrap().writes,
            if uncertain { 4 } else { 6 }
        );
        assert_eq!(
            fan_rows(&r).last().unwrap()["progress"],
            if uncertain { 30 } else { 50 }
        );
    }
}

// Managed keeps the observed AC selector on both external classes, so a
// manual fan write under Performance is admitted on a limited USB-C adapter
// with the same commands and fresh per-command class admission.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_limited_source_keeps_the_performance_selector() {
    let r = fan_rig(2, 0);
    r.port.state.lock().unwrap().actual = 10;
    r.get().await;
    let applied = r.request(fan_request(180, Some(1), 3700)).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    assert_eq!(r.port.state.lock().unwrap().selectors, [2, 2]);
    r.origin.stop();
    r.stop().await;
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_owned_limited_source_performance_is_stored_without_consuming_release() {
    let r = fan_rig(0, 0);
    r.port.state.lock().unwrap().actual = 10;
    r.get().await;
    let applied = r.request(fan_request(180, Some(1), 3700)).await;
    assert_eq!(applied["result"]["status"], 1);
    let before = fan_rows(&r).len();
    let perf = r.set(181, value(1, 1)).await;
    // Managed defers on both external sources, so a limited adapter gets the
    // same Stored deferral as AC while the owned fan row stays outstanding.
    assert_eq!(perf["result"]["status"], 0);
    assert_eq!(
        perf["result"]["message"],
        "Performance intent was stored and will be applied when fans return to Automatic."
    );
    assert_eq!(fan_rows(&r).len(), before);
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    let desired = r.files.document("manual-overrides.json");
    assert_eq!(desired["overrides"]["performance"], 1);
    assert_eq!(desired["overrides"]["fanControl"]["fixedSpeedRpm"], 3700);
    r.origin.stop();
    r.stop().await;
    assert_eq!(r.port.state.lock().unwrap().writes, 6);
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 50);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_ac_manual_deferral_is_stored_but_cleared_fan_handback_blocks() {
    // AC with saved and observed manual fans: managed's deferral reply.
    let r = fan_rig(0, 0);
    r.get().await;
    assert_eq!(
        r.request(fan_request(180, Some(1), 3700)).await["result"]["status"],
        1
    );
    let deferred = r.set(181, value(2, 1)).await;
    assert_eq!(deferred["result"]["status"], 0);
    assert_eq!(
        deferred["result"]["message"],
        "Performance intent was stored and will be applied when fans return to Automatic."
    );
    r.origin.stop();
    r.stop().await;

    // The fan override is cleared but its handback is still outstanding: no
    // manual fan intent remains, so the reply is Blocked with the prefix.
    // An unsupported saved Custom keeps the return from writing anything.
    let r = fan_rig(0, 0);
    r.get().await;
    assert_eq!(
        r.request(fan_request(180, Some(1), 3700)).await["result"]["status"],
        1
    );
    let mut custom = value(5, 1);
    custom["customPerformance"] = json!({"cpu":2,"gpu":2,"maximumFans":false});
    assert_eq!(r.set(181, custom).await["result"]["status"], 2);
    let cleared = r.request(fan_request(182, None, 0)).await;
    assert_eq!(cleared["result"]["status"], 2);
    assert!(
        cleared["result"]["message"]
            .as_str()
            .unwrap()
            .starts_with("Fan override was cleared. Hardware result: ")
    );
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 30);
    assert!(r.storage.outstanding_fan());
    assert_eq!(
        r.files.document("manual-overrides.json")["overrides"]["fanControl"],
        Value::Null
    );
    let writes = r.port.state.lock().unwrap().writes;
    let blocked = r.set(183, value(2, 1)).await;
    assert_eq!(blocked["result"]["status"], 2);
    assert_eq!(
        blocked["result"]["message"],
        "Performance override intent was stored. Hardware result: OpenBlade still owns a fan change that has not been handed back to firmware Automatic; no thermal setter was submitted."
    );
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    assert_eq!(
        r.files.document("manual-overrides.json")["overrides"]["performance"],
        2
    );
    r.port.state.lock().unwrap().selectors = [0, 0];
    r.origin.stop();
    assert!(r.source.lock().await.drain().await.is_ok());
}

// Apply-on-handback. Managed leaves manual fans through the saved preset's own
// 0D/02 pair (ApplyDirectAutomaticFanTransitionOutcomeAsync), so the stored
// performance intent is applied by the fan request that returns the fans to
// Automatic, in one pair, with the fan journal and per-setter admission.
#[cfg(feature = "service-fan-mutation-v2")]
const DEFERRED: &str =
    "Performance intent was stored and will be applied when fans return to Automatic.";
/// OpenBlade-owned Fixed fans under `selector`, then a `preset` override that
/// managed defers on external power. Four entry setters, no deferral write.
#[cfg(feature = "service-fan-mutation-v2")]
async fn handback_rig(selector: u8, actual: u8, preset: i32) -> Rig {
    handback_rig_with(selector, actual, value(preset, 1)).await
}
/// `handback_rig` with any deferred performance request.
#[cfg(feature = "service-fan-mutation-v2")]
async fn handback_rig_with(selector: u8, actual: u8, request: Value) -> Rig {
    let r = fan_rig(selector, 0);
    r.port.state.lock().unwrap().actual = actual;
    r.get().await;
    let owned = r.request(fan_request(230, Some(1), 3700)).await;
    assert_eq!(owned["result"]["status"], 1, "{owned}");
    let deferred = r.set(231, request).await;
    assert_eq!(deferred["result"]["status"], 0, "{deferred}");
    assert_eq!(deferred["result"]["message"], DEFERRED);
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    assert!(r.storage.outstanding_fan());
    r
}
#[cfg(feature = "service-fan-mutation-v2")]
fn request_rows(r: &Rig, n: u32) -> Vec<Value> {
    let id = format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}");
    fan_rows(r)
        .into_iter()
        .filter(|row| row["request"] == id)
        .collect()
}
/// The thermal-mode setters a request sent, in order.
#[cfg(feature = "service-fan-mutation-v2")]
fn mode_setters(r: &Rig, from: usize) -> Vec<Vec<u8>> {
    r.port.state.lock().unwrap().sent[from..]
        .iter()
        .filter(|(class, command, _, _)| (*class, *command) == (13, 2))
        .map(|(_, _, payload, _)| payload.clone())
        .collect()
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_recipes_run_within_the_recipe_budget_and_refuse_one_they_cannot_finish() {
    // Fixed fans from Balanced on paused fixture time: the observation, the
    // 07/8C read, four setters with their power reads and the final pair are
    // 13 exchanges, plus 275 ms of spacing. At 150 ms a transfer that is about
    // 4.6 s: past FRAME_TIMEOUT, inside the 6 s recipe budget.
    for (ms, applied) in [(150, true), (500, false)] {
        let r = fan_rig(0, 0);
        r.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(ms));
        let out = r.request(fan_request(240, Some(1), 3700)).await;
        if applied {
            assert_eq!(out["result"]["status"], 1, "{out}");
            assert_eq!(r.port.state.lock().unwrap().writes, 4);
        } else {
            // About 3.1 s for the first three exchanges leaves too little for
            // the other ten: stored, with no setter, and the Prepared row
            // settled as known not submitted.
            assert_eq!(out["result"]["status"], 2, "{out}");
            assert_eq!(
                out["result"]["message"],
                "Fan override intent was stored. Hardware result: The remaining request time cannot cover this recipe at the measured device pace; no fan setter was submitted."
            );
            assert_eq!(r.port.state.lock().unwrap().writes, 0);
            let rows: Vec<Value> = fan_rows(&r)
                .iter()
                .map(|row| row["progress"].clone())
                .collect();
            assert_eq!(rows, [json!(0), json!(6)]);
            assert!(!r.storage.pending());
            assert!(!r.storage.outstanding_fan());
            assert_eq!(
                r.files.document("manual-overrides.json")["overrides"]["fans"],
                1
            );
        }
        r.port.state.lock().unwrap().latency = None;
        r.origin.stop();
        let _ = r.source.lock().await.drain().await;
    }
}
/// A fan rig on adapter reading `actual` (Windows agrees by default) with an
/// optional saved persistent performance override.
#[cfg(feature = "service-fan-mutation-v2")]
fn saved_override_fan_rig(actual: u8, selector: u8, performance: Option<i32>) -> Rig {
    let files = MemoryFiles::default();
    files.profiles(1);
    if let Some(performance) = performance {
        files.put(
            "manual-overrides.json",
            json!({"schemaVersion":10,"overrides":{"performance":performance,"fans":null,"fanControl":null,"lighting":null,"customPerformance":null,"gpuClockTuning":null}}),
        );
    }
    let r = Rig::new(files);
    *r.custody.origin_for_fan.lock().unwrap() = Some(r.origin.clone());
    {
        let mut s = r.port.state.lock().unwrap();
        s.selectors = [selector; 2];
        s.fans = [0; 2];
        s.actual = actual;
    }
    if actual == 0 {
        r.metadata.store(1, Ordering::Release);
    }
    r
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_requests_managed_rejects_store_nothing_and_submit_nothing() {
    // Managed `SetFanOverrideCoreAsync` on classified power, before the
    // override is persisted: `FanControlPowerPolicy.IsAvailable` for a
    // requested mode, then `ValidateThermalOverrideCombination` with the saved
    // performance override (not filtered by source eligibility) or the
    // profile's preset, and the requested mode or the profile's fans.
    const NOT_VALID: &str = "The performance preset is not valid for this power source.";
    const UNAVAILABLE: &str =
        "The selected fan mode is unavailable on this model and power source.";
    const COMBINATION: &str =
        "The performance and fan combination is not supported by the active validation policy.";
    let cases = [
        // Battery with a saved Silent: every request, a clear included.
        (0, 6, Some(0), Some(1), NOT_VALID),
        (0, 6, Some(0), Some(3), NOT_VALID),
        (0, 6, Some(0), Some(0), NOT_VALID),
        (0, 6, Some(0), None, NOT_VALID),
        // The power policy runs first.
        (0, 6, Some(0), Some(2), UNAVAILABLE),
        (0, 6, None, Some(2), UNAVAILABLE),
        // AC with a saved Battery Saver: ManualCurve.
        (15, 3, Some(3), Some(2), COMBINATION),
    ];
    for (actual, selector, saved, mode, message) in cases {
        let r = saved_override_fan_rig(actual, selector, saved);
        r.get().await;
        let before = r.files.read("manual-overrides.json").unwrap();
        let out = r.request(fan_request(250, mode, 3700)).await;
        let case = format!("{actual} {saved:?} {mode:?}");
        assert_eq!(out["result"]["status"], 3, "{case}: {out}");
        assert_eq!(
            out["result"]["message"],
            format!("The requested intent was rejected: {message}"),
            "{case}"
        );
        assert_eq!(
            r.files.read("manual-overrides.json").unwrap(),
            before,
            "{case}"
        );
        assert_eq!(r.port.state.lock().unwrap().writes, 0, "{case}");
        assert!(fan_rows(&r).is_empty(), "{case}");
        r.origin.stop();
        let _ = r.source.lock().await.drain().await;
    }
    // Managed admits these: Fixed on battery without a saved override, and
    // Fixed on AC with a saved Battery Saver. Both are stored; native has no
    // fan recipe under the battery selectors, so neither is applied.
    for (actual, selector, saved) in [(0, 6, None), (15, 3, Some(3))] {
        let r = saved_override_fan_rig(actual, selector, saved);
        r.get().await;
        let out = r.request(fan_request(251, Some(1), 3700)).await;
        assert_eq!(out["result"]["status"], 2, "{actual} {saved:?}: {out}");
        assert_eq!(r.port.state.lock().unwrap().writes, 0);
        assert_eq!(
            r.files.document("manual-overrides.json")["overrides"]["fans"],
            1
        );
        r.origin.stop();
        let _ = r.source.lock().await.drain().await;
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_on_a_battery_reading_windows_does_not_confirm_is_blocked() {
    // Managed maps an adapter Battery reply under Windows AC or unknown power
    // to ExternalUnknown and blocks the request before any intent is stored.
    for windows in [Some(false), None] {
        for mode in [Some(1), None] {
            let r = saved_override_fan_rig(0, 6, None);
            r.port.state.lock().unwrap().windows_on_battery = Some(windows);
            r.get().await;
            let out = r.request(fan_request(252, mode, 3700)).await;
            assert_eq!(out["result"]["status"], 2, "{windows:?} {mode:?}: {out}");
            assert_eq!(
                out["result"]["message"],
                crate::installed::readiness::thermal_mutation::UNCLASSIFIED_BLOCKED
            );
            assert!(r.files.read("manual-overrides.json").unwrap().is_none());
            assert_eq!(r.port.state.lock().unwrap().writes, 0);
            assert!(fan_rows(&r).is_empty());
            r.origin.stop();
            let _ = r.source.lock().await.drain().await;
        }
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_is_checked_against_a_temporary_performance_override() {
    // Managed's `Automation.PerformanceOverride` is the persistent override
    // or, without one, an unexpired temporary one.
    let r = saved_override_fan_rig(15, 3, None);
    r.get().await;
    let saved = r.set(254, value(3, 0)).await;
    assert!(
        r.files.read("manual-overrides.json").unwrap().is_none()
            || r.files.document("manual-overrides.json")["overrides"]["performance"].is_null(),
        "{saved}"
    );
    let writes = r.port.state.lock().unwrap().writes;
    let out = r.request(fan_request(253, Some(2), 3700)).await;
    assert_eq!(out["result"]["status"], 3, "{saved} {out}");
    assert_eq!(
        out["result"]["message"],
        "The requested intent was rejected: The performance and fan combination is not supported by the active validation policy."
    );
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    assert!(fan_rows(&r).is_empty());
    r.origin.stop();
    let _ = r.source.lock().await.drain().await;
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_under_windows_battery_is_checked_as_battery() {
    // Managed `ResolveCurrentPowerSourceAsync` returns Battery whenever
    // Windows reports battery, without sampling the adapter, and
    // `SetFanOverrideCoreAsync` checks the request against that source. An
    // adapter reading full power does not make it AC: these were stored.
    const NOT_VALID: &str = "The performance preset is not valid for this power source.";
    const UNAVAILABLE: &str =
        "The selected fan mode is unavailable on this model and power source.";
    for (saved, mode, message) in [
        (Some(3), Some(2), UNAVAILABLE),
        (Some(0), Some(1), NOT_VALID),
    ] {
        let r = saved_override_fan_rig(15, 3, saved);
        r.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
        r.get().await;
        let before = r.files.read("manual-overrides.json").unwrap();
        let out = r.request(fan_request(255, mode, 3700)).await;
        let case = format!("{saved:?} {mode:?}");
        assert_eq!(out["result"]["status"], 3, "{case}: {out}");
        assert_eq!(
            out["result"]["message"],
            format!("The requested intent was rejected: {message}"),
            "{case}"
        );
        assert_eq!(
            r.files.read("manual-overrides.json").unwrap(),
            before,
            "{case}"
        );
        assert_eq!(r.port.state.lock().unwrap().writes, 0, "{case}");
        assert!(fan_rows(&r).is_empty(), "{case}");
        r.origin.stop();
        let _ = r.source.lock().await.drain().await;
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_under_windows_battery_drops_an_ac_temporary_from_its_check() {
    // A temporary Silent saved on AC, then Windows battery with the adapter
    // still reading full power. The reading's class keeps the temporary
    // (`observe_power(0)`), but managed's source is Battery, where the
    // temporary has expired: the check uses the battery profile, which admits
    // Fixed. Native stores the request and, having no fan recipe under Silent,
    // applies nothing. Managed applies it (see the Fixed-entry gap in the fan
    // guide).
    let r = saved_override_fan_rig(15, 0, None);
    r.get().await;
    let saved = r.set(206, value(0, 0)).await;
    assert_eq!(saved["result"]["status"], 1, "{saved}");
    assert_eq!(r.storage.current_performance_override(0).unwrap(), Some(0));
    r.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
    let writes = r.port.state.lock().unwrap().writes;
    let out = r.request(fan_request(257, Some(1), 3700)).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert!(
        out["result"]["message"]
            .as_str()
            .unwrap()
            .starts_with("Fan override intent was stored."),
        "{out}"
    );
    assert_eq!(
        r.files.document("manual-overrides.json")["overrides"]["fans"],
        1
    );
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    r.origin.stop();
    let _ = r.source.lock().await.drain().await;
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_contradicted_battery_reading_leaves_an_ac_temporary_in_place() {
    // Managed maps an adapter battery reply under Windows AC to
    // ExternalUnknown and blocks the request; that classification never
    // becomes current, so a temporary saved on AC survives it. Both the
    // performance and the fan path block before observing the reading.
    for fan in [false, true] {
        let r = saved_override_fan_rig(15, 0, None);
        r.get().await;
        let saved = r.set(208, value(2, 0)).await;
        assert_eq!(saved["result"]["status"], 1, "{fan}: {saved}");
        {
            let mut s = r.port.state.lock().unwrap();
            s.actual = 0;
            s.windows_on_battery = Some(Some(false));
        }
        let out = if fan {
            r.request(fan_request(259, Some(1), 3700)).await
        } else {
            r.set(209, value(1, 1)).await
        };
        assert_eq!(out["result"]["status"], 2, "{fan}: {out}");
        assert_eq!(
            out["result"]["message"],
            crate::installed::readiness::thermal_mutation::UNCLASSIFIED_BLOCKED,
            "{fan}"
        );
        assert_eq!(
            r.storage.current_performance_override(0).unwrap(),
            Some(2),
            "{fan}"
        );
        r.origin.stop();
        let _ = r.source.lock().await.drain().await;
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_request_on_a_limited_adapter_is_checked_with_the_usb_c_profile() {
    // Managed's source for a limited adapter under Windows AC is UsbC, so the
    // check uses the USB-C profile: its Battery Saver rejects ManualCurve,
    // which the plugged-in Balanced profile would admit.
    let files = MemoryFiles::default();
    files.profiles_with(1, Some(3));
    let r = Rig::new(files);
    *r.custody.origin_for_fan.lock().unwrap() = Some(r.origin.clone());
    {
        let mut s = r.port.state.lock().unwrap();
        s.selectors = [0; 2];
        s.fans = [0; 2];
        s.actual = 10;
    }
    r.get().await;
    let before = r.files.read("manual-overrides.json").unwrap();
    let out = r.request(fan_request(260, Some(2), 3700)).await;
    assert_eq!(out["result"]["status"], 3, "{out}");
    assert_eq!(
        out["result"]["message"],
        "The requested intent was rejected: The performance and fan combination is not supported by the active validation policy."
    );
    assert_eq!(r.files.read("manual-overrides.json").unwrap(), before);
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
    assert!(fan_rows(&r).is_empty());
    r.origin.stop();
    let _ = r.source.lock().await.drain().await;
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn temporary_charge_runs_within_its_request_budget_at_slow_transfers() {
    // The worker bounds a temporary-charge job by its 6 s request deadline, as
    // it does every battery job. At 250 and 400 ms a transfer the exchange runs
    // past the 3 s observation bound, and that bound alone stopped it after the
    // flags setter, with the journal still pending.
    for ms in [250, 400] {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        r.port.state.lock().unwrap().latency = Some(std::time::Duration::from_millis(ms));
        let out = r
            .request(battery_request(702, "setFullChargeOverride", json!(true)))
            .await;
        r.port.state.lock().unwrap().latency = None;
        assert_eq!(out["result"]["status"], 1, "{ms} ms: {out}");
        assert_eq!(r.port.state.lock().unwrap().writes, 1, "{ms} ms");
        assert!(!r.storage.pending(), "{ms} ms");
        let _ = r.source.lock().await.drain().await;
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_applies_the_deferred_preset_on_full_and_limited_adapters() {
    for (actual, source) in [(15, 0), (10, 2)] {
        for (selector, preset, name) in [(0, 2, "Performance"), (2, 1, "Balanced")] {
            let r = handback_rig(selector, actual, preset).await;
            let destination = if preset == 2 { 2 } else { 0 };
            let sent = r.port.state.lock().unwrap().sent.len();
            let out = r.request(fan_request(232, None, 0)).await;
            assert_eq!(out["result"]["status"], 1, "{out}");
            assert_eq!(
                out["result"]["message"],
                format!(
                    "Fans returned to Automatic with {name} performance, confirmed by independent paired readback."
                )
            );
            // One direct pair: the saved preset with Automatic fans, no fan pair first.
            assert_eq!(
                mode_setters(&r, sent),
                [[1, 1, destination, 0], [1, 2, destination, 0]]
            );
            let commands: Vec<_> = {
                let state = r.port.state.lock().unwrap();
                assert_eq!(
                    (state.writes, state.selectors, state.fans),
                    (6, [destination; 2], [0; 2])
                );
                state.sent[sent..].iter().map(|s| (s.0, s.1)).collect()
            };
            // A fresh 07/8C read precedes each setter.
            for (index, command) in commands.iter().enumerate() {
                if *command == (13, 2) {
                    assert_eq!(commands[index - 1], (7, 0x8c));
                }
            }
            let rows = request_rows(&r, 232);
            let progress: Vec<_> = rows.iter().map(|row| row["progress"].clone()).collect();
            assert_eq!(progress, [0, 10, 11, 12, 13, 50]);
            for row in &rows {
                assert_eq!(row["fan"]["selector"], destination);
                assert_eq!(row["fan"]["source"], source);
                assert_eq!(row["fan"]["intent"]["mode"], 0);
            }
            assert!(!r.storage.outstanding_fan());
            assert!(!r.storage.pending());
            assert_eq!(
                r.files.document("manual-overrides.json")["overrides"]["performance"],
                preset
            );
            let observed = fresh_observe(&r).await;
            assert_eq!(observed["status"]["confirmedState"]["performance"], preset);
            assert_eq!(observed["status"]["confirmedState"]["fans"], 0);
            // Nothing is left to hand back at Stop.
            r.origin.stop();
            r.stop().await;
            assert_eq!(r.port.state.lock().unwrap().writes, 6);
        }
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_power_class_change_before_or_between_setters_follows_managed() {
    // Managed `AdmitOrdinaryThermalCommandAsync` admits every setter on a fresh
    // external reading that agrees with Windows AC, so a full/limited change
    // keeps the explicit Performance destination and the pair completes.
    // Battery disagrees and stops it without replay.
    for (actual, changed) in [(15, 10), (10, 15), (15, 0)] {
        for between in [false, true] {
            let r = handback_rig(0, actual, 2).await;
            let reads = r.port.state.lock().unwrap().power_reads;
            // One read resolves the source and the destination, then one per setter.
            r.port.state.lock().unwrap().change_power =
                Some((reads + if between { 3 } else { 2 }, changed));
            let out = r.request(fan_request(232, None, 0)).await;
            let rows = request_rows(&r, 232);
            // The destination preset is journaled before any possible submission.
            assert_eq!(rows[0]["progress"], 0);
            assert_eq!(rows[0]["fan"]["selector"], 2);
            // The row keeps the first class.
            assert_eq!(rows[0]["fan"]["source"], if actual == 15 { 0 } else { 2 });
            if changed != 0 {
                assert_eq!(out["result"]["status"], 1, "{out}");
                assert_eq!(
                    out["result"]["message"],
                    "Fans returned to Automatic with Performance performance, confirmed by independent paired readback."
                );
                {
                    let state = r.port.state.lock().unwrap();
                    assert_eq!(
                        (state.writes, state.selectors, state.fans),
                        (6, [2; 2], [0; 2])
                    );
                }
                r.origin.stop();
                r.stop().await;
                continue;
            }
            assert_eq!(out["result"]["status"], 2, "{out}");
            let writes = r.port.state.lock().unwrap().writes;
            if between {
                assert_eq!(writes, 5);
                assert_eq!(rows.last().unwrap()["progress"], 11);
                assert!(r.storage.pending());
                assert_eq!(
                    out["result"]["message"],
                    "Fan submission or owned release remains unresolved; further writes are fenced."
                );
                // The possibly submitted pair is never replayed or replaced.
                assert_eq!(
                    r.request(fan_request(233, None, 0)).await["result"]["status"],
                    2
                );
                assert_eq!(r.set(234, value(2, 1)).await["result"]["status"], 2);
                assert_eq!(r.port.state.lock().unwrap().writes, 5);
                r.origin.stop();
                assert!(r.source.lock().await.drain().await.is_err());
                assert_eq!(r.port.state.lock().unwrap().writes, 5);
            } else {
                assert_eq!(writes, 4);
                assert_eq!(rows.last().unwrap()["progress"], 6);
                assert!(!r.storage.pending());
                assert!(r.storage.outstanding_fan());
                {
                    let state = r.port.state.lock().unwrap();
                    assert_eq!((state.selectors, state.fans), ([0; 2], [1; 2]));
                }
                // Stop hands the owner back through its own selector, never the
                // saved preset: shutdown does not apply saved settings.
                r.port.state.lock().unwrap().actual = actual;
                r.origin.stop();
                r.stop().await;
                let state = r.port.state.lock().unwrap();
                assert_eq!(
                    (state.writes, state.selectors, state.fans),
                    (6, [0; 2], [0; 2])
                );
            }
        }
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_class_change_to_another_profile_destination_stops() {
    // No explicit override: the return applies the plugged-in profile's
    // Performance. A limited adapter between the setters selects the USB-C
    // profile's Balanced, so the pair stops without replay.
    let files = MemoryFiles::default();
    files.profiles_with(2, Some(1));
    let r = Rig::new(files);
    *r.custody.origin_for_fan.lock().unwrap() = Some(r.origin.clone());
    {
        let mut s = r.port.state.lock().unwrap();
        s.selectors = [0; 2];
        s.fans = [0; 2];
    }
    r.get().await;
    let owned = r.request(fan_request(230, Some(1), 3700)).await;
    assert_eq!(owned["result"]["status"], 1, "{owned}");
    let reads = r.port.state.lock().unwrap().power_reads;
    r.port.state.lock().unwrap().change_power = Some((reads + 3, 10));
    let out = r.request(fan_request(232, None, 0)).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert_eq!(request_rows(&r, 232)[0]["fan"]["selector"], 2);
    assert_eq!(r.port.state.lock().unwrap().writes, 5);
    assert!(r.storage.pending());
    r.origin.stop();
    assert!(r.source.lock().await.drain().await.is_err());
    assert_eq!(r.port.state.lock().unwrap().writes, 5);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_readback_mismatch_or_bad_echo_fences_without_replay() {
    for mismatch in [true, false] {
        let r = handback_rig(0, 15, 2).await;
        {
            let mut state = r.port.state.lock().unwrap();
            if mismatch {
                state.mismatch_after = Some(6);
            } else {
                state.corrupt_ack = Some(5);
            }
        }
        let sent = r.port.state.lock().unwrap().sent.len();
        let out = r.request(fan_request(232, None, 0)).await;
        assert_eq!(out["result"]["status"], 2, "{out}");
        assert_eq!(
            out["result"]["message"],
            "Fan submission or owned release remains unresolved; further writes are fenced."
        );
        let setters = mode_setters(&r, sent);
        assert_eq!(setters[0], [1, 1, 2, 0]);
        assert_eq!(setters.len(), if mismatch { 2 } else { 1 });
        let rows = request_rows(&r, 232);
        assert_eq!(
            rows.last().unwrap()["progress"],
            if mismatch { 13 } else { 10 }
        );
        assert!(rows.iter().all(|row| row["fan"]["selector"] == 2));
        assert!(r.storage.pending());
        let writes = r.port.state.lock().unwrap().writes;
        assert_eq!(writes, if mismatch { 6 } else { 5 });
        assert_eq!(
            r.request(fan_request(233, None, 0)).await["result"]["status"],
            2
        );
        assert_eq!(r.set(234, value(2, 1)).await["result"]["status"], 2);
        assert_eq!(r.port.state.lock().unwrap().writes, writes);
        r.origin.stop();
        assert!(r.source.lock().await.drain().await.is_err());
        assert_eq!(r.port.state.lock().unwrap().writes, writes);
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_canceled_mid_pair_then_stop_retains_ambiguity() {
    let r = handback_rig(0, 15, 2).await;
    let held = Arc::new(AtomicBool::new(false));
    r.port.state.lock().unwrap().held_write = Some((5, held.clone()));
    let sent = r.port.state.lock().unwrap().sent.len();
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(232, None, 0)).unwrap(),
    )
    .unwrap();
    let mut mutation = r.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let end = Instant::now() + HANG_GUARD;
    while r.port.state.lock().unwrap().writes == 4 {
        assert!(Instant::now() < end);
        tokio::select! {result=&mut mutation=>panic!("held handback completed early: {}",result.is_ok()),_=tokio::time::sleep(Duration::from_millis(5))=>{}}
    }
    drop(mutation);
    r.origin.stop();
    let mut source = r.source.lock().await;
    let mut draining = source.drain();
    assert!(
        tokio::time::timeout(Duration::from_millis(20), &mut draining)
            .await
            .is_err()
    );
    assert!(!r.custody.retired.load(Ordering::Acquire));
    held.store(true, Ordering::Release);
    assert!(
        tokio::time::timeout(HANG_GUARD, draining)
            .await
            .unwrap()
            .is_err()
    );
    drop(source);
    let state = r.port.state.lock().unwrap();
    assert_eq!(state.opened, state.retired);
    assert_eq!(state.writes, 5);
    drop(state);
    assert_eq!(mode_setters(&r, sent), [[1, 1, 2, 0]]);
    let rows = request_rows(&r, 232);
    assert_eq!(rows.last().unwrap()["progress"], 10);
    assert_eq!(rows.last().unwrap()["fan"]["selector"], 2);
    assert!(r.storage.pending());
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_rechecks_the_saved_preset_before_each_setter() {
    // The profile's Performance preset is the saved intent; it changes to
    // Balanced after the first setter, before the second setter's power read.
    let r = fan_rig_with(0, 0, 2);
    r.get().await;
    assert_eq!(
        r.request(fan_request(230, Some(1), 3700)).await["result"]["status"],
        1
    );
    let reads = r.port.state.lock().unwrap().power_reads;
    let files = r.files.clone();
    r.port.state.lock().unwrap().on_power = Some(Arc::new(move |read| {
        if read == reads + 3 {
            files.profiles_with(1, Some(1));
        }
    }));
    let out = r.request(fan_request(232, None, 0)).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert_eq!(r.port.state.lock().unwrap().writes, 5);
    let rows = request_rows(&r, 232);
    assert_eq!(rows.last().unwrap()["progress"], 11);
    assert!(rows.iter().all(|row| row["fan"]["selector"] == 2));
    assert!(r.storage.pending());
    r.origin.stop();
    assert!(r.source.lock().await.drain().await.is_err());
    assert_eq!(r.port.state.lock().unwrap().writes, 5);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_applies_a_saved_silent_profile_unless_fans_were_automatic() {
    // Each case also runs with a deferred Performance override as its control.
    for stored in [false, true] {
        // Without the override the return writes the Silent profile's own
        // pair, as managed `ApplyDirectAutomaticFanTransitionOutcomeAsync` does.
        let r = fan_rig_with(0, 0, 0);
        r.get().await;
        assert_eq!(
            r.request(fan_request(230, Some(1), 3700)).await["result"]["status"],
            1
        );
        if stored {
            assert_eq!(r.set(231, value(2, 1)).await["result"]["message"], DEFERRED);
        }
        let sent = r.port.state.lock().unwrap().sent.len();
        let out = r.request(fan_request(232, None, 0)).await;
        assert_eq!(out["result"]["status"], 1, "{out}");
        let selector = if stored { 2 } else { 5 };
        assert_eq!(
            mode_setters(&r, sent),
            [[1, 1, selector, 0], [1, 2, selector, 0]]
        );
        if !stored {
            assert_eq!(
                out["result"]["message"],
                "Fans returned to Automatic with Silent performance, confirmed by independent paired readback."
            );
        }
        r.origin.stop();
        r.stop().await;
        let selector = if stored { 2 } else { 0 };

        // Saved fans were already Automatic: managed applies no preset when the
        // request does not leave saved manual intent, even over external manual fans.
        let files = MemoryFiles::default();
        files.profiles(1);
        let mut fan = crate::installed::readiness::thermal_storage::legacy_fan(1);
        fan["fixedSpeedRpm"] = json!(3700);
        files.put("manual-overrides.json", json!({"schemaVersion":8,"overrides":{"performance":2,"fans":if stored {json!(1)} else {Value::Null},"fanControl":if stored {fan} else {Value::Null},"lighting":null,"customPerformance":null,"gpuClockTuning":null}}));
        let r = Rig::new(files);
        *r.custody.origin_for_fan.lock().unwrap() = Some(r.origin.clone());
        {
            let mut s = r.port.state.lock().unwrap();
            s.selectors = [0; 2];
            s.fans = [1; 2];
        }
        r.get().await;
        let out = r.request(fan_request(240, Some(0), 3700)).await;
        assert_eq!(out["result"]["status"], 1, "{out}");
        assert_eq!(
            mode_setters(&r, 0),
            [[1, 1, selector, 0], [1, 2, selector, 0]]
        );
        r.origin.stop();
        r.stop().await;
    }

    // A saved GPU clock effect has no native pair: the fans return under the
    // observed selector and the reply says the saved preset was not applied.
    let r = fan_rig(0, 0);
    r.get().await;
    assert_eq!(
        r.request(fan_request(230, Some(1), 3700)).await["result"]["status"],
        1
    );
    let mut desired = value(2, 1);
    desired["gpuClockTuning"] =
        json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
    assert_eq!(r.set(231, desired).await["result"]["status"], 2);
    let sent = r.port.state.lock().unwrap().sent.len();
    let out = r.request(fan_request(232, None, 0)).await;
    assert_eq!(out["result"]["status"], 1, "{out}");
    assert_eq!(mode_setters(&r, sent), [[1, 1, 0, 0], [1, 2, 0, 0]]);
    assert!(out["result"]["message"].as_str().unwrap().ends_with(
        " The saved performance intent was not applied: GPU clock handling is incomplete."
    ));
    r.origin.stop();
    r.stop().await;
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_deferral_promises_every_native_destination() {
    // A Silent profile under owned Fixed fans: clearing the override selects
    // Silent, which the fan return writes, so the deferral promises the apply
    // as it does for a Performance override.
    for (n, request, selector) in [(231, Value::Null, 5), (233, value(2, 1), 2)] {
        let r = fan_rig_with(0, 0, 0);
        r.get().await;
        assert_eq!(
            r.request(fan_request(230, Some(1), 3700)).await["result"]["status"],
            1
        );
        let out = r.set(n, request).await;
        assert_eq!(out["result"]["message"], DEFERRED, "{out}");
        assert_eq!(out["result"]["status"], 0, "{out}");
        // No thermal setter beyond the four entry setters.
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        let sent = r.port.state.lock().unwrap().sent.len();
        let back = r.request(fan_request(240, None, 0)).await;
        assert_eq!(back["result"]["status"], 1, "{back}");
        assert_eq!(
            mode_setters(&r, sent),
            [[1, 1, selector, 0], [1, 2, selector, 0]]
        );
        r.origin.stop();
        r.stop().await;
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_expires_a_stale_class_temporary_gpu_override_first() {
    // A temporary Performance override with a GPU effect, kept for the full
    // adapter, expires when the adapter becomes limited. The limited class's
    // profile Performance then applies, whichever storage read runs first.
    for read_desired_first in [false, true] {
        let files = MemoryFiles::default();
        files.profiles_with(1, Some(2));
        let r = Rig::new(files);
        *r.custody.origin_for_fan.lock().unwrap() = Some(r.origin.clone());
        {
            let mut s = r.port.state.lock().unwrap();
            s.selectors = [0; 2];
            s.fans = [0; 2];
        }
        r.get().await;
        assert_eq!(
            r.request(fan_request(230, Some(1), 3700)).await["result"]["status"],
            1
        );
        let mut temporary = value(2, 0);
        temporary["gpuClockTuning"] =
            json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
        assert_eq!(r.set(231, temporary).await["result"]["status"], 2);
        r.port.state.lock().unwrap().actual = 10;
        if read_desired_first {
            assert!(saved_desired(&r.storage, 2).unwrap().is_some());
        }
        let sent = r.port.state.lock().unwrap().sent.len();
        let out = r.request(fan_request(232, None, 0)).await;
        assert_eq!(
            out["result"]["message"],
            "Fans returned to Automatic with Performance performance, confirmed by independent paired readback.",
            "{out}"
        );
        assert_eq!(mode_setters(&r, sent), [[1, 1, 2, 0], [1, 2, 2, 0]]);
        r.origin.stop();
        r.stop().await;
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_handback_retry_after_a_refused_return_reports_the_unapplied_preset() {
    // The first return is refused before any setter (battery) after
    // its Automatic intent was saved. Managed's trigger is request-level, so the
    // retry leaves no saved manual intent and keeps the observed selector; its
    // reply says the saved preset was not applied.
    let r = handback_rig(0, 15, 2).await;
    let reads = r.port.state.lock().unwrap().power_reads;
    r.port.state.lock().unwrap().change_power = Some((reads + 2, 0));
    let first = r.request(fan_request(232, None, 0)).await;
    assert_eq!(first["result"]["status"], 2, "{first}");
    assert_eq!(request_rows(&r, 232).last().unwrap()["progress"], 6);
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    r.port.state.lock().unwrap().actual = 15;
    let sent = r.port.state.lock().unwrap().sent.len();
    let retry = r.request(fan_request(233, None, 0)).await;
    assert_eq!(retry["result"]["status"], 1, "{retry}");
    assert_eq!(mode_setters(&r, sent), [[1, 1, 0, 0], [1, 2, 0, 0]]);
    assert_eq!(
        retry["result"]["message"],
        "Fan setters completed and independent mode was confirmed. Target RPM and rotation are not readable on this backend. The saved Performance performance intent was not applied: submit a new performance request to apply it."
    );
    // The new performance request the reply names applies it directly.
    let applied = r.set(234, value(2, 1)).await;
    assert_eq!(applied["result"]["status"], 1, "{applied}");
    {
        let state = r.port.state.lock().unwrap();
        assert_eq!((state.selectors, state.fans), ([2; 2], [0; 2]));
    }
    // A matching confirmed selector adds no note.
    let again = r.request(fan_request(235, None, 0)).await;
    assert_eq!(again["result"]["status"], 1, "{again}");
    assert_eq!(
        again["result"]["message"],
        "Automatic mode independently observed; no fan setter was submitted and no ownership was established."
    );
    r.origin.stop();
    r.stop().await;
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn manual_fans_under_any_selector_return_through_the_saved_destination() {
    // Manual fans under Silent (5), Battery Saver (3), Battery Balanced (6)
    // and Custom (4): managed reads each as FanMode.Maximum and applies the
    // saved destination's own pair from it, as from selectors 0 and 2.
    for selector in [5, 3, 6, 4] {
        let r = handback_rig(0, 15, 2).await;
        r.port.state.lock().unwrap().selectors = [selector; 2];
        let sent = r.port.state.lock().unwrap().sent.len();
        let out = r.request(fan_request(232, None, 0)).await;
        assert_eq!(out["result"]["status"], 1, "{selector}: {out}");
        assert_eq!(
            out["result"]["message"],
            "Fans returned to Automatic with Performance performance, confirmed by independent paired readback.",
            "{selector}"
        );
        assert_eq!(
            mode_setters(&r, sent),
            [[1, 1, 2, 0], [1, 2, 2, 0]],
            "{selector}"
        );
        assert_eq!(request_rows(&r, 232).last().unwrap()["progress"], 50);
        {
            let state = r.port.state.lock().unwrap();
            assert_eq!((state.selectors, state.fans), ([2; 2], [0; 2]));
        }
        r.origin.stop();
        r.stop().await;
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn an_unsupported_saved_custom_keeps_fans_manual_and_says_so() {
    // Managed's fan return applies the saved Custom, whose capability check
    // refuses High/High before any write: fans stay manual, the fan intent is
    // saved and the reply names the refused destination.
    for (request, message) in [
        (fan_request(232, None, 0), "Fan override was cleared."),
        (
            fan_request(232, Some(0), 3700),
            "Fan override intent was stored.",
        ),
    ] {
        let r = fan_rig(0, 0);
        r.port.state.lock().unwrap().actual = 15;
        r.get().await;
        let owned = r.request(fan_request(230, Some(1), 3700)).await;
        assert_eq!(owned["result"]["status"], 1, "{owned}");
        let mut custom = value(5, 1);
        custom["customPerformance"] = json!({"cpu":2,"gpu":2,"maximumFans":false});
        assert_eq!(r.set(231, custom).await["result"]["status"], 2);
        let sent = r.port.state.lock().unwrap().sent.len();
        let out = r.request(request).await;
        assert_eq!(out["result"]["status"], 2, "{out}");
        assert_eq!(
            out["result"]["message"],
            format!(
                "{message} Hardware result: Fans could not return to Automatic with the saved Custom performance intent. Performance value is not supported by the verified capability table."
            )
        );
        assert!(request_rows(&r, 232).is_empty());
        assert!(mode_setters(&r, sent).is_empty());
        {
            let state = r.port.state.lock().unwrap();
            assert_eq!((state.writes, state.fans), (4, [1; 2]));
        }
        r.origin.stop();
        r.stop().await;
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_fan_return_carrying_a_battery_preset_needs_windows_ac_presence() {
    // A Battery Saver override deferred under Fixed fans on AC power. The
    // return applies it through managed `SetPerformanceAsync`
    // (`allowBattery: false`), which refuses Windows battery before any
    // write, battery presets included: nothing is prepared or written.
    let r = handback_rig(0, 15, 3).await;
    r.port.state.lock().unwrap().windows_on_battery = Some(Some(true));
    let sent = r.port.state.lock().unwrap().sent.len();
    let out = r.request(fan_request(232, None, 0)).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert!(request_rows(&r, 232).is_empty(), "{out}");
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    assert!(mode_setters(&r, sent).is_empty());
    r.origin.stop();
    r.stop().await;
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_correction_fresh_epoch_auto_recovery_and_true_noop_close_original_owner() {
    for already_auto in [false, true] {
        for resume in [false, true] {
            let r = fan_rig(2, 0);
            r.get().await;
            assert_eq!(
                r.request(fan_request(180, Some(1), 3700)).await["result"]["status"],
                1
            );
            let original = fan_rows(&r).last().unwrap().clone();
            let bytes = r.files.read("thermal-mutation.journal").unwrap();
            if resume {
                r.custody.lifecycle.ingress(
                    windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
                    4,
                );
                assert_eq!(
                    r.request(fan_request(181, Some(0), 3700)).await["result"]["status"],
                    2
                );
                assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), bytes);
                r.custody.lifecycle.ingress(
                    windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
                    18,
                );
            } else {
                r.custody.lifecycle.ingress(
                    windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE,
                    0,
                );
            }
            let writes = r.port.state.lock().unwrap().writes;
            r.custody.fresh_refuse.store(true, Ordering::Release);
            assert_eq!(
                r.request(fan_request(182, Some(0), 3700)).await["result"]["status"],
                2
            );
            r.custody.fresh_refuse.store(false, Ordering::Release);
            let captures = r.factory_calls.load(Ordering::Acquire);
            assert_eq!(
                r.request_as(
                    fan_request(183, Some(0), 3700),
                    USER,
                    "S-1-5-21-111-222-333-1002"
                )
                .await["result"]["status"],
                2
            );
            assert_eq!(
                r.request_as(
                    fan_request(184, Some(0), 3700),
                    Caller {
                        session: 8,
                        control_session: Some(8),
                        ..USER
                    },
                    SID
                )
                .await["result"]["status"],
                2
            );
            let foreign_performance = json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000186","value":value(1, 1)});
            assert_eq!(
                r.request_as(foreign_performance, USER, "S-1-5-21-111-222-333-1002")
                    .await["result"]["status"],
                2
            );
            assert_eq!(r.factory_calls.load(Ordering::Acquire), captures);
            assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), bytes);
            assert_eq!(r.port.state.lock().unwrap().writes, writes);
            if already_auto {
                r.port.state.lock().unwrap().fans = [0; 2];
            }
            let out = r.request(fan_request(185, Some(0), 3700)).await;
            assert_eq!(out["result"]["status"], 1, "{out}");
            let rows = fan_rows(&r);
            if already_auto {
                let mut expected = original;
                expected["progress"] = json!(51);
                assert_eq!(*rows.last().unwrap(), expected);
                assert!(
                    out["result"]["message"]
                        .as_str()
                        .unwrap()
                        .contains("no fan setter")
                );
            } else {
                assert_eq!(rows.last().unwrap()["progress"], 50);
            }
            assert_eq!(
                r.port.state.lock().unwrap().writes,
                writes + if already_auto { 0 } else { 2 }
            );
            let before_stop = r.files.read("thermal-mutation.journal").unwrap();
            r.origin.stop();
            r.stop().await;
            assert_eq!(
                r.files.read("thermal-mutation.journal").unwrap(),
                before_stop
            );
        }
    }
}
// Firmware or another controller moved OpenBlade-owned manual fans to Automatic
// under a selector the owner did not record. Automatic fans are firmware's own
// handback state, so both an explicit Automatic request and Stop settle the owner
// with 51 and no setter, as managed clears ownership on confirmed Automatic.
#[cfg(feature = "service-fan-mutation-v2")]
async fn foreign_selector_rig(owner: u8, observed: [u8; 2], fans: [u8; 2], actual: u8) -> Rig {
    // The deferred preset is the observed one, so a return has nothing to apply.
    let r = handback_rig(owner, actual, if owner == 0 { 2 } else { 1 }).await;
    let mut s = r.port.state.lock().unwrap();
    s.selectors = observed;
    s.fans = fans;
    drop(s);
    r
}
#[cfg(feature = "service-fan-mutation-v2")]
fn owner_row(r: &Rig, progress: u8) -> Value {
    let mut row = request_rows(r, 230).last().unwrap().clone();
    assert_eq!(row["progress"], 30);
    row["progress"] = json!(progress);
    row
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_noop_closes_an_owner_recorded_under_another_selector_without_a_setter() {
    for (owner, observed, name, recorded) in [
        (0, 2, "Performance", "Balanced"),
        (2, 0, "Balanced", "Performance"),
    ] {
        for actual in [15, 10] {
            for explicit in [true, false] {
                let r = foreign_selector_rig(owner, [observed; 2], [0; 2], actual).await;
                let closed = owner_row(&r, 51);
                let request = if explicit {
                    fan_request(232, Some(0), 3700)
                } else {
                    fan_request(232, None, 0)
                };
                let out = r.request(request).await;
                assert_eq!(out["result"]["status"], 1, "{out}");
                assert_eq!(
                    out["result"]["message"],
                    format!(
                        "Fans and {name} performance already matched the requested state; no fan setter was submitted and no ownership was established. OpenBlade's earlier fan change under {recorded} was released without a fan setter: firmware already reports Automatic fans under {name}."
                    )
                );
                assert_eq!(r.port.state.lock().unwrap().writes, 4);
                assert_eq!(*fan_rows(&r).last().unwrap(), closed);
                assert!(request_rows(&r, 232).is_empty());
                assert!(!r.storage.outstanding_fan());
                assert!(!r.storage.pending());
                // Nothing remains for Stop, and no selector is written back.
                let journal = r.files.read("thermal-mutation.journal").unwrap();
                r.origin.stop();
                r.stop().await;
                assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), journal);
                let state = r.port.state.lock().unwrap();
                assert_eq!(
                    (state.writes, state.selectors, state.fans),
                    (4, [observed; 2], [0; 2])
                );
            }
        }
    }
}
/// The history refuses a performance row while a fan change this Service
/// owns is outstanding, so the store refuses to append one: the journal is
/// unchanged and still opens, and owned Stop still hands the fans back.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn a_performance_row_is_refused_while_an_owned_fan_change_is_outstanding() {
    use openblade_domain::thermal_mutation::{Destination, Preset, Progress};
    let r = fan_rig(0, 0);
    r.get().await;
    let fan = r.request(fan_request(180, Some(1), 3700)).await;
    assert_eq!(fan["result"]["status"], 1, "{fan}");
    assert!(r.storage.outstanding_fan());
    let journal = r.files.read("thermal-mutation.journal").unwrap();
    let recorded = r.storage.record(
        "aaaaaaaa-bbbb-cccc-dddd-000000000181",
        "owner",
        Destination::Preset(Preset::Balanced),
        "intent",
        &None,
        1,
        1,
        Progress::Prepared,
    );
    assert_eq!(
        recorded.unwrap_err().kind(),
        io::ErrorKind::PermissionDenied
    );
    assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), journal);
    assert!(Storage::open(Box::new(r.files.clone()), SID).is_ok());
    r.origin.stop();
    r.stop().await;
    assert!(!r.storage.outstanding_fan());
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_stop_settles_an_owner_already_automatic_under_any_selector_without_a_setter() {
    for (owner, observed) in [(0, 2), (2, 0), (0, 0), (2, 2)] {
        for actual in [15, 10] {
            let r = foreign_selector_rig(owner, [observed; 2], [0; 2], actual).await;
            let closed = owner_row(&r, 51);
            r.origin.stop();
            r.stop().await;
            assert_eq!(*fan_rows(&r).last().unwrap(), closed);
            assert!(!r.storage.outstanding_fan());
            assert!(!r.storage.pending());
            let state = r.port.state.lock().unwrap();
            assert_eq!(
                (state.writes, state.selectors, state.fans),
                (4, [observed; 2], [0; 2])
            );
        }
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_foreign_selector_close_refuses_an_ambiguous_or_undecodable_readback() {
    // Disagreeing channels and mode 7 with manual fans (not decoded) prove no
    // Automatic handback: no row, no setter, the owner stays outstanding.
    for (observed, fans) in [([2, 0], [0; 2]), ([7; 2], [1; 2])] {
        let r = foreign_selector_rig(0, observed, fans, 15).await;
        let (owned, closed) = (owner_row(&r, 30), owner_row(&r, 51));
        let out = r.request(fan_request(232, Some(0), 3700)).await;
        assert_eq!(out["result"]["status"], 2, "{out}");
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        assert_eq!(*fan_rows(&r).last().unwrap(), owned);
        assert!(r.storage.outstanding_fan());
        // A later unambiguous Automatic readback settles it without a setter.
        r.port.state.lock().unwrap().selectors = [2; 2];
        r.port.state.lock().unwrap().fans = [0; 2];
        let out = r.request(fan_request(233, Some(0), 3700)).await;
        assert_eq!(out["result"]["status"], 1, "{out}");
        assert_eq!(*fan_rows(&r).last().unwrap(), closed);
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        r.origin.stop();
        r.stop().await;

        // Stop keeps its one-shot fail-closed refusal on the same readback.
        let r = foreign_selector_rig(0, observed, fans, 15).await;
        let owned = owner_row(&r, 30);
        r.origin.stop();
        assert!(r.source.lock().await.drain().await.is_err());
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        assert_eq!(*fan_rows(&r).last().unwrap(), owned);
        assert!(r.storage.outstanding_fan());
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_manual_readback_under_another_selector_keeps_its_existing_outcomes() {
    // Regression guard, unchanged by the no-setter close: manual fans are not
    // firmware's handback state. Stop refuses a selector it did not record.
    let r = foreign_selector_rig(0, [2; 2], [1; 2], 15).await;
    let owned = owner_row(&r, 30);
    r.origin.stop();
    assert!(r.source.lock().await.drain().await.is_err());
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
    assert_eq!(*fan_rows(&r).last().unwrap(), owned);
    assert!(r.storage.outstanding_fan());
    // An explicit return still leaves through the saved preset's own pair.
    let r = foreign_selector_rig(0, [2; 2], [1; 2], 15).await;
    let sent = r.port.state.lock().unwrap().sent.len();
    let out = r.request(fan_request(232, Some(0), 3700)).await;
    assert_eq!(out["result"]["status"], 1, "{out}");
    assert_eq!(mode_setters(&r, sent), [[1, 1, 2, 0], [1, 2, 2, 0]]);
    let progress: Vec<_> = request_rows(&r, 232)
        .iter()
        .map(|row| row["progress"].clone())
        .collect();
    assert_eq!(progress, [0, 10, 11, 12, 13, 50]);
    assert!(!r.storage.outstanding_fan());
    r.origin.stop();
    r.stop().await;
    assert_eq!(r.port.state.lock().unwrap().writes, 6);
}
/// The journals both no-setter closes produce are the shared vector the domain
/// reader and managed `WindowsNativeThermalHistory` consume. The v3 contract
/// is the battery-policy producer's, so this runs in those lanes only.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn fan_foreign_selector_close_journals_match_the_shared_vector() {
    let v: Value = serde_json::from_str(include_str!(
        "../../../../../tests/data/fan-history-v3-foreign-selector-automatic.json"
    ))
    .unwrap();
    let journal = |r: &Rig| {
        String::from_utf8(r.files.read("thermal-mutation.journal").unwrap().unwrap()).unwrap()
    };
    // The 51 row repeats the owner's record, so a close after a change to the
    // limited adapter or to battery, or under Custom or HyperBoost, writes the
    // same bytes: source stays 0. Explicit HyperBoost stays fenced.
    for (now, observed) in [
        (15, [2; 2]),
        (10, [2; 2]),
        (0, [3; 2]),
        (15, [4; 2]),
        (15, [7; 2]),
    ] {
        for stop in [false, true] {
            if !stop && observed == [7; 2] {
                continue;
            }
            let r = power_change_rig(15, now, observed, [0; 2]).await;
            // Admitted Medium/High levels; High/High would leave no explicit baseline.
            r.port.state.lock().unwrap().custom_levels = [1, 2];
            assert_eq!(journal(&r), v["owned"].as_str().unwrap());
            if !stop {
                // On battery the saved persistent Performance override
                // rejects the explicit request; Stop then closes the owner
                // with the same bytes.
                let out = r.request(fan_request(232, Some(0), 3700)).await;
                let status = if now == 0 { 3 } else { 1 };
                assert_eq!(out["result"]["status"], status, "{out}");
            }
            r.origin.stop();
            r.stop().await;
            assert_eq!(journal(&r), v["closed"].as_str().unwrap());
            assert_eq!(r.port.state.lock().unwrap().writes, 4);
        }
    }
}
// After a power class change, or on battery where no native plan exists, an
// unambiguous Automatic readback is still firmware's own handback state.
#[cfg(feature = "service-fan-mutation-v2")]
async fn power_change_rig(start: u8, now: u8, observed: [u8; 2], fans: [u8; 2]) -> Rig {
    let r = foreign_selector_rig(0, observed, fans, start).await;
    r.port.state.lock().unwrap().actual = now;
    if now != start {
        // Windows reports every source change (PBT_APMPOWERSTATUSCHANGE), which
        // advances the lifecycle epoch the owner's proof pins.
        r.custody.lifecycle.ingress(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
        );
    }
    r
}
/// Adapter classes as fixture 07/8C levels: full 15, limited 10, battery 0.
#[cfg(feature = "service-fan-mutation-v2")]
const POWER_CHANGES: [(u8, u8, [u8; 2]); 6] = [
    (15, 10, [2; 2]),
    (10, 15, [2; 2]),
    (15, 0, [3; 2]),
    (10, 0, [6; 2]),
    (15, 0, [0; 2]),
    // Automatic fans under firmware Silent have no fan plan on either adapter.
    (15, 15, [5; 2]),
];
/// The exact reply for each `POWER_CHANGES` case. The owner recorded Balanced
/// and the saved preset is Performance: it is claimed only where Performance is
/// confirmed, a different firmware selector is named, and the unapplied preset
/// is reported where its class selects one. On battery the saved persistent
/// Performance override fails managed `ValidateThermalOverrideCombination`, so
/// the explicit request is rejected before anything is stored or read.
#[cfg(feature = "service-fan-mutation-v2")]
const POWER_CHANGE_REPLIES: [&str; 6] = [
    "Fans and Performance performance already matched the requested state; no fan setter was submitted and no ownership was established. OpenBlade's earlier fan change under Balanced was released without a fan setter: firmware already reports Automatic fans under Performance.",
    "Fans and Performance performance already matched the requested state; no fan setter was submitted and no ownership was established. OpenBlade's earlier fan change under Balanced was released without a fan setter: firmware already reports Automatic fans under Performance.",
    BATTERY_OVERRIDE_REJECTED,
    BATTERY_OVERRIDE_REJECTED,
    BATTERY_OVERRIDE_REJECTED,
    "Automatic mode independently observed; no fan setter was submitted and no ownership was established. OpenBlade's earlier fan change under Balanced was released without a fan setter: firmware already reports Automatic fans under Silent. The saved Performance performance intent was not applied: submit a new performance request to apply it.",
];
/// A fan request on battery while a saved persistent AC preset remains.
#[cfg(feature = "service-fan-mutation-v2")]
const BATTERY_OVERRIDE_REJECTED: &str =
    "The requested intent was rejected: The performance preset is not valid for this power source.";
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_automatic_after_a_power_change_or_on_battery_settles_without_a_setter() {
    for ((start, now, observed), reply) in POWER_CHANGES.into_iter().zip(POWER_CHANGE_REPLIES) {
        let r = power_change_rig(start, now, observed, [0; 2]).await;
        let closed = owner_row(&r, 51);
        let out = r.request(fan_request(232, Some(0), 3700)).await;
        assert_eq!(out["result"]["message"], reply, "{start}->{now}");
        if now == 0 {
            // Rejected: the owner and its row stay; Stop settles it below.
            assert_eq!(out["result"]["status"], 3, "{start}->{now} {out}");
            assert_eq!(*fan_rows(&r).last().unwrap(), owner_row(&r, 30));
            assert!(r.storage.outstanding_fan());
            assert_eq!(r.port.state.lock().unwrap().writes, 4);
            r.origin.stop();
            r.stop().await;
            assert_eq!(*fan_rows(&r).last().unwrap(), closed);
            continue;
        }
        assert_eq!(out["result"]["status"], 1, "{start}->{now} {out}");
        // The row keeps the owner's recorded source, not the current class.
        assert_eq!(*fan_rows(&r).last().unwrap(), closed);
        assert_eq!(closed["fan"]["source"], if start == 15 { 0 } else { 2 });
        assert!(request_rows(&r, 232).is_empty());
        assert!(!r.storage.outstanding_fan());
        let journal = r.files.read("thermal-mutation.journal").unwrap();
        r.origin.stop();
        r.stop().await;
        assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), journal);
        assert_eq!(r.port.state.lock().unwrap().writes, 4);

        let r = power_change_rig(start, now, observed, [0; 2]).await;
        let closed = owner_row(&r, 51);
        r.origin.stop();
        r.stop().await;
        assert_eq!(*fan_rows(&r).last().unwrap(), closed, "{start}->{now}");
        assert!(!r.storage.outstanding_fan());
        let state = r.port.state.lock().unwrap();
        assert_eq!(
            (state.writes, state.selectors, state.fans),
            (4, observed, [0; 2])
        );
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_power_change_close_refuses_an_ambiguous_readback() {
    for (start, now, observed) in POWER_CHANGES {
        // Channels disagree: no observation, no row, no setter.
        let split = [observed[0], if observed[0] == 2 { 0 } else { 2 }];
        let r = power_change_rig(start, now, split, [0; 2]).await;
        let (owned, closed) = (owner_row(&r, 30), owner_row(&r, 51));
        let out = r.request(fan_request(232, Some(0), 3700)).await;
        assert_eq!(out["result"]["status"], 2, "{start}->{now} {out}");
        assert_eq!(*fan_rows(&r).last().unwrap(), owned);
        assert!(r.storage.outstanding_fan());
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        // Once both channels agree, the same request settles without a setter;
        // on battery the saved Performance override rejects it instead.
        r.port.state.lock().unwrap().selectors = observed;
        let out = r.request(fan_request(233, Some(0), 3700)).await;
        if now == 0 {
            assert_eq!(out["result"]["message"], BATTERY_OVERRIDE_REJECTED);
            assert_eq!(*fan_rows(&r).last().unwrap(), owned);
        } else {
            assert_eq!(out["result"]["status"], 1, "{start}->{now} {out}");
            assert_eq!(*fan_rows(&r).last().unwrap(), closed);
        }
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        r.origin.stop();
        r.stop().await;

        // Stop keeps its one-shot fail-closed refusal on the same readback.
        let r = power_change_rig(start, now, split, [0; 2]).await;
        let owned = owner_row(&r, 30);
        r.origin.stop();
        assert!(r.source.lock().await.drain().await.is_err());
        assert_eq!(*fan_rows(&r).last().unwrap(), owned);
        assert!(r.storage.outstanding_fan());
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_manual_readback_after_a_power_change_keeps_its_existing_outcomes() {
    // Manual fans are not firmware's handback state.
    for now in [10, 0] {
        // With the POWEREVENT, Stop takes the settle-only permit, which cannot
        // journal a release. Without one (a PD renegotiation with AC still
        // online, or a class change after STOP_PENDING) the owner's own permit
        // still authorizes Stop. On the limited adapter it hands back with the
        // same commands (managed owned Stop admits any fresh external reading
        // that agrees with Windows AC); battery refuses before 31, so the row
        // stays a clean 30, not a pending release.
        for event in [true, false] {
            let r = if event {
                power_change_rig(15, now, [0; 2], [1; 2]).await
            } else {
                let r = foreign_selector_rig(0, [0; 2], [1; 2], 15).await;
                r.port.state.lock().unwrap().actual = now;
                r
            };
            let owned = owner_row(&r, 30);
            r.origin.stop();
            if now == 10 && !event {
                r.stop().await;
                assert_eq!(fan_rows(&r).last().unwrap()["progress"], 50);
                assert_eq!(fan_rows(&r).last().unwrap()["fan"]["source"], 0);
                assert!(!r.storage.outstanding_fan());
                let state = r.port.state.lock().unwrap();
                assert_eq!((state.writes, state.fans), (6, [0; 2]));
                continue;
            }
            assert!(r.source.lock().await.drain().await.is_err());
            assert_eq!(*fan_rows(&r).last().unwrap(), owned, "{now} {event}");
            assert!(r.storage.outstanding_fan());
            assert!(!r.storage.pending(), "{now} {event}");
            assert_eq!(r.port.state.lock().unwrap().writes, 4);
        }
        // An explicit return writes its pair on the limited adapter. On battery
        // the saved persistent Performance override rejects it before storage.
        let r = power_change_rig(15, now, [0; 2], [1; 2]).await;
        let out = r.request(fan_request(232, Some(0), 3700)).await;
        if now == 10 {
            assert_eq!(out["result"]["status"], 1, "{out}");
            assert_eq!(r.port.state.lock().unwrap().writes, 6);
            assert_eq!(request_rows(&r, 232).last().unwrap()["progress"], 50);
            assert!(!r.storage.outstanding_fan());
            r.origin.stop();
            r.stop().await;
        } else {
            assert_eq!(out["result"]["message"], BATTERY_OVERRIDE_REJECTED);
            assert_eq!(r.port.state.lock().unwrap().writes, 4);
            assert_eq!(*fan_rows(&r).last().unwrap(), owner_row(&r, 30));
            assert!(r.storage.outstanding_fan());
        }
    }
}
// Managed releases fan ownership on any confirmed Automatic fan mode, which
// includes Custom with Automatic fans and HyperBoost (mode 7, Automatic fans).
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_custom_automatic_and_hyperboost_release_the_owner_without_a_setter() {
    // Fixture levels: 1 Medium, 2 High. High/High Custom Automatic is rejected
    // by the native getter admission (and by managed's read), so the explicit
    // path has no baseline; Stop reads no levels and still settles.
    for (observed, levels) in [([4; 2], [1, 2]), ([4; 2], [2, 2]), ([7; 2], [1, 2])] {
        let r = foreign_selector_rig(0, observed, [0; 2], 15).await;
        r.port.state.lock().unwrap().custom_levels = levels;
        let (owned, closed) = (owner_row(&r, 30), owner_row(&r, 51));
        let out = r.request(fan_request(232, Some(0), 3700)).await;
        if (observed, levels) == ([4; 2], [1, 2]) {
            assert_eq!(out["result"]["status"], 1, "{out}");
            assert_eq!(
                out["result"]["message"],
                "Automatic mode independently observed; no fan setter was submitted and no ownership was established. OpenBlade's earlier fan change under Balanced was released without a fan setter: firmware already reports Automatic fans under Custom. The saved Performance performance intent was not applied: submit a new performance request to apply it."
            );
            assert_eq!(*fan_rows(&r).last().unwrap(), closed);
            assert!(!r.storage.outstanding_fan());
            // The reply publishes the levels it read, not a null Custom.
            let status = r.get().await;
            let confirmed = &status["status"]["confirmedState"];
            assert_eq!(confirmed["performance"], 5, "{status}");
            assert_eq!(
                confirmed["customPerformance"],
                json!({"cpu": 1, "gpu": 2, "maximumFans": false}),
                "{status}"
            );
        } else {
            // High/High Custom has no admitted baseline, and HyperBoost stays
            // fenced on the explicit path: the reply would publish an
            // observation outside managed read parity.
            assert_eq!(out["result"]["status"], 2, "{observed:?} {levels:?} {out}");
            assert_eq!(*fan_rows(&r).last().unwrap(), owned);
            assert!(r.storage.outstanding_fan());
        }
        assert!(request_rows(&r, 232).is_empty());
        assert_eq!(r.port.state.lock().unwrap().writes, 4);
        r.origin.stop();
        r.stop().await;
        assert_eq!(*fan_rows(&r).last().unwrap(), closed);
        assert!(!r.storage.outstanding_fan());

        // Stop alone settles both, after a power event too, with no setter.
        for now in [15, 10] {
            let r = power_change_rig(15, now, observed, [0; 2]).await;
            r.port.state.lock().unwrap().custom_levels = levels;
            r.origin.stop();
            r.stop().await;
            assert_eq!(*fan_rows(&r).last().unwrap(), closed, "{observed:?} {now}");
            assert!(!r.storage.outstanding_fan());
            let state = r.port.state.lock().unwrap();
            assert_eq!(
                (state.writes, state.selectors, state.fans),
                (4, observed, [0; 2])
            );
        }
    }
}
// Stop settles on the 0D/82 pair alone: an unclassified 07/8C source is not
// part of the Automatic handback. The explicit path admits unclassified power
// only with Windows AC presence, so without it the request stays refused.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_stop_settle_does_not_need_a_classified_power_source() {
    let r = foreign_selector_rig(0, [2; 2], [0; 2], 15).await;
    let (owned, closed) = (owner_row(&r, 30), owner_row(&r, 51));
    {
        let mut s = r.port.state.lock().unwrap();
        s.recommended = 0;
        s.windows_on_battery = Some(Some(true));
    }
    let out = r.request(fan_request(232, Some(0), 3700)).await;
    assert_eq!(out["result"]["status"], 2, "{out}");
    assert_eq!(*fan_rows(&r).last().unwrap(), owned);
    r.origin.stop();
    r.stop().await;
    assert_eq!(*fan_rows(&r).last().unwrap(), closed);
    assert!(!r.storage.outstanding_fan());
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
}
/// Windows notices that advance the lifecycle epoch, as SCM delivers them.
#[cfg(feature = "service-fan-mutation-v2")]
type LifecycleCase = (&'static str, u8, u8, [u8; 2], &'static [(u32, u32)]);
#[cfg(feature = "service-fan-mutation-v2")]
const LIFECYCLE_EVENTS: [LifecycleCase; 5] = {
    use windows_sys::Win32::System::Services::{
        SERVICE_CONTROL_POWEREVENT as POWER, SERVICE_CONTROL_SESSIONCHANGE as SESSION,
    };
    [
        ("unplug", 15, 0, [3; 2], &[(POWER, 10)]),
        ("limited adapter", 15, 10, [2; 2], &[(POWER, 10)]),
        ("full adapter", 10, 15, [2; 2], &[(POWER, 10)]),
        // WTS_SESSION_LOCK then WTS_SESSION_UNLOCK.
        (
            "lock and unlock",
            15,
            15,
            [2; 2],
            &[(SESSION, 7), (SESSION, 8)],
        ),
        // PBT_APMSUSPEND then PBT_APMRESUMEAUTOMATIC.
        (
            "sleep and resume",
            15,
            15,
            [2; 2],
            &[(POWER, 4), (POWER, 18)],
        ),
    ]
};
#[cfg(feature = "service-fan-mutation-v2")]
async fn lifecycle_rig(start: u8, now: u8, observed: [u8; 2], events: &[(u32, u32)]) -> Rig {
    let r = foreign_selector_rig(0, observed, [0; 2], start).await;
    r.port.state.lock().unwrap().actual = now;
    for (code, event) in events {
        r.custody.lifecycle.ingress(*code, *event);
    }
    r
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_stop_settles_after_a_power_or_session_event_without_a_setter() {
    for (name, start, now, observed, events) in LIFECYCLE_EVENTS {
        for explicit in [false, true] {
            let r = lifecycle_rig(start, now, observed, events).await;
            let closed = owner_row(&r, 51);
            if explicit {
                let out = r.request(fan_request(232, Some(0), 3700)).await;
                if now == 0 {
                    // The saved persistent Performance override rejects it;
                    // Stop still settles the owner.
                    assert_eq!(out["result"]["message"], BATTERY_OVERRIDE_REJECTED);
                    assert_eq!(*fan_rows(&r).last().unwrap(), owner_row(&r, 30));
                } else {
                    assert_eq!(out["result"]["status"], 1, "{name} {out}");
                    assert_eq!(*fan_rows(&r).last().unwrap(), closed, "{name}");
                }
            }
            r.origin.stop();
            r.stop().await;
            assert_eq!(*fan_rows(&r).last().unwrap(), closed, "{name}");
            assert!(!r.storage.outstanding_fan(), "{name}");
            assert!(!r.storage.pending(), "{name}");
            let state = r.port.state.lock().unwrap();
            assert_eq!(
                (state.writes, state.selectors, state.fans),
                (4, observed, [0; 2]),
                "{name}"
            );
        }
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_settle_stop_fences_without_fresh_stop_admission() {
    let reasons = [
        "ambiguous readback",
        "changed logon",
        "changed SID",
        "competing controller",
        "lost origin trust",
        "lost custody integrity",
        "changed generation",
        "suspended",
        "session not the unlocked console",
    ];
    for (reason, name) in reasons.into_iter().enumerate() {
        let r = power_change_rig(15, 10, [2; 2], [0; 2]).await;
        let owned = owner_row(&r, 30);
        let c = &r.custody;
        match reason {
            0 => r.port.state.lock().unwrap().selectors = [2, 0],
            1 => c.authentication.store(2, Ordering::Release),
            2 => *c.current_sid.lock().unwrap() = "S-1-5-21-111-222-333-1002".into(),
            3 => c.fresh_refuse.store(true, Ordering::Release),
            5 => c.integrity_lost.store(true, Ordering::Release),
            6 => c.generation.store(8, Ordering::Release),
            7 => c.lifecycle.ingress(
                windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
                4,
            ),
            8 => c.current_console.store(false, Ordering::Release),
            _ => {}
        }
        r.origin.stop();
        if reason == 4 {
            r.origin.lose_trust();
        }
        assert!(r.source.lock().await.drain().await.is_err(), "{name}");
        assert_eq!(*fan_rows(&r).last().unwrap(), owned, "{name}");
        assert!(r.storage.outstanding_fan(), "{name}");
        assert_eq!(r.port.state.lock().unwrap().writes, 4, "{name}");
    }
}
// The settle permit rechecks its whole admission on every read checkpoint and
// before the 51 row, not only at capture. Each change lands while the first
// 0D/82 reply of the Stop read is held.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_settle_stop_rechecks_admission_during_the_read() {
    use windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT as POWER;
    let cases = [
        ("competing controller", true),
        ("console", true),
        ("SID", true),
        ("logon", true),
        ("epoch", true),
        ("suspend", true),
        ("saved fan configuration", true),
        ("epoch on the owner path", false),
        ("none", true),
    ];
    for (name, event_first) in cases {
        let r = foreign_selector_rig(0, [2; 2], [0; 2], 15).await;
        if event_first {
            r.custody.lifecycle.ingress(POWER, 10);
        }
        let (owned, closed) = (owner_row(&r, 30), owner_row(&r, 51));
        let release = Arc::new(AtomicBool::new(false));
        r.port.state.lock().unwrap().held_read = Some(release.clone());
        let sent = r.port.state.lock().unwrap().sent.len();
        r.origin.stop();
        let drain = async { r.source.lock().await.drain().await };
        let change = async {
            let deadline = std::time::Instant::now() + HANG_GUARD;
            while r.port.state.lock().unwrap().sent.len() == sent {
                assert!(std::time::Instant::now() < deadline, "{name}");
                tokio::time::sleep(Duration::from_millis(1)).await;
            }
            let c = &r.custody;
            match name {
                "competing controller" => c.fresh_refuse.store(true, Ordering::Release),
                "console" => c.current_console.store(false, Ordering::Release),
                "SID" => *c.current_sid.lock().unwrap() = "S-1-5-21-111-222-333-1002".into(),
                "logon" => c.authentication.store(2, Ordering::Release),
                "epoch" | "epoch on the owner path" => c.lifecycle.ingress(POWER, 10),
                "suspend" => c.lifecycle.ingress(POWER, 4),
                "saved fan configuration" => {
                    let mut v = r.files.document("manual-overrides.json");
                    v["overrides"]["fans"] = json!(3);
                    r.files.put("manual-overrides.json", v);
                }
                _ => {}
            }
            r.port.state.lock().unwrap().held_read = None;
            release.store(true, Ordering::Release);
        };
        let (drained, ()) = tokio::join!(drain, change);
        if name == "none" {
            drained.unwrap();
            assert_eq!(*fan_rows(&r).last().unwrap(), closed);
            continue;
        }
        assert!(drained.is_err(), "{name}");
        assert_eq!(*fan_rows(&r).last().unwrap(), owned, "{name}");
        assert!(r.storage.outstanding_fan(), "{name}");
        assert_eq!(r.port.state.lock().unwrap().writes, 4, "{name}");
    }
}
// Result publication is a separate admission: a fence that lands after the
// 51 row is written still refuses the confirmed result, so Stop reports the
// handback as unconfirmed instead of claiming it.
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_settle_stop_result_needs_fresh_admission() {
    let r = power_change_rig(15, 10, [2; 2], [0; 2]).await;
    let closed = owner_row(&r, 51);
    let c = r.custody.clone();
    r.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        c.fresh_refuse.store(true, Ordering::Release);
    }));
    r.origin.stop();
    // Held, so the refusal is the fence's and not a loaded host's deadline.
    assert!(r.drain().await.is_err());
    // The 51 row is durable; it is not replayed or rolled back.
    assert_eq!(*fan_rows(&r).last().unwrap(), closed);
    assert!(!r.storage.outstanding_fan());
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_settle_only_permit_cannot_reach_a_setter() {
    // Manual fans after an event: the settle path reads, then cannot journal
    // the release stage, so no setter is sent and the owner stays at 30. The
    // lock and unlock case keeps the owner's selector and source, so only the
    // permit stands between it and the owner's Automatic release pair.
    for (now, events) in [
        (10, &[][..]),
        (0, &[][..]),
        (
            15,
            &[
                (
                    windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE,
                    7,
                ),
                (
                    windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE,
                    8,
                ),
            ][..],
        ),
    ] {
        let r = power_change_rig(15, now, [0; 2], [1; 2]).await;
        for (code, event) in events {
            r.custody.lifecycle.ingress(*code, *event);
        }
        let owned = owner_row(&r, 30);
        let sent = r.port.state.lock().unwrap().sent.len();
        r.origin.stop();
        assert!(r.source.lock().await.drain().await.is_err());
        assert_eq!(*fan_rows(&r).last().unwrap(), owned);
        assert!(r.storage.outstanding_fan());
        let state = r.port.state.lock().unwrap();
        assert_eq!(state.writes, 4);
        assert!(
            state.sent[sent..]
                .iter()
                .all(|(class, command, _, _)| *class != 13 || !matches!(*command, 1 | 2)),
            "{:?}",
            &state.sent[sent..]
        );
    }
    // The permit itself admits reads and the 51 close only.
    let r = power_change_rig(15, 10, [2; 2], [0; 2]).await;
    let journal = r.files.read("thermal-mutation.journal").unwrap();
    r.origin.stop();
    let permit = r.runtime.settle_permit_for_tests().unwrap();
    assert!(permit.authorize().is_some());
    assert!(permit.cleanup() && permit.is_fan());
    let record = permit.fan_record().unwrap();
    let baseline = crate::discovery::performance::ObservedPerformance::PerformanceAutomatic;
    for progress in [
        0, 6, 10, 11, 12, 13, 14, 15, 16, 17, 30, 31, 40, 41, 42, 43, 50,
    ] {
        assert!(permit.record_fan(progress).is_err(), "{progress}");
    }
    assert!(permit.prepare_fan(record).is_err());
    assert!(permit.resolve_fan(2, baseline).is_err());
    assert!(permit.confirm_fan_noop(2, baseline).is_err());
    assert!(
        permit
            .resolve(2, baseline, &AtomicBool::new(false))
            .is_err()
    );
    assert!(
        permit
            .record(openblade_domain::thermal_mutation::Progress::FirstIntent)
            .is_err()
    );
    assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), journal);
    assert_eq!(r.port.state.lock().unwrap().writes, 4);
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_correction_cancelled_positive_worker_then_refusal_releases_latest_exact_owner() {
    let r = fan_rig(2, 0);
    r.get().await;
    r.request(fan_request(190, Some(1), 3700)).await;
    let entered = Arc::new(AtomicBool::new(false));
    let release = Arc::new(AtomicBool::new(false));
    let hook_entered = entered.clone();
    let hook_release = release.clone();
    r.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        hook_entered.store(true, Ordering::Release);
        let deadline = std::time::Instant::now() + HANG_GUARD;
        while !hook_release.load(Ordering::Acquire) {
            assert!(std::time::Instant::now() < deadline);
            std::thread::sleep(Duration::from_millis(2));
        }
    }));
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(191, Some(1), 3700)).unwrap(),
    )
    .unwrap();
    let mut future = r.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let deadline = Instant::now() + HANG_GUARD;
    while !entered.load(Ordering::Acquire) {
        assert!(Instant::now() < deadline);
        tokio::select! { result=&mut future => panic!("retirement hold ended early: {}", result.is_ok()), _=tokio::time::sleep(Duration::from_millis(5))=>{} }
    }
    let owner = fan_rows(&r).last().unwrap().clone();
    assert_eq!(owner["request"], "aaaaaaaa-bbbb-cccc-dddd-000000000191");
    assert_eq!(owner["progress"], 30);
    drop(future);
    r.port.state.lock().unwrap().on_mutation_retired = None;
    release.store(true, Ordering::Release);
    r.files.capacity_refuse.store(true, Ordering::Release);
    // Saved but unapplied fan intent is Blocked, as managed reports it.
    let refused = r.request(fan_request(192, Some(1), 3700)).await;
    assert_eq!(refused["result"]["status"], 2);
    assert!(
        refused["result"]["message"]
            .as_str()
            .unwrap()
            .starts_with("Fan override intent was stored. Hardware result: ")
    );
    let saved = r.files.read("manual-overrides.json").unwrap();
    let writes = r.port.state.lock().unwrap().writes;
    r.origin.stop();
    r.stop().await;
    let mut expected = owner;
    expected["progress"] = json!(50);
    assert_eq!(*fan_rows(&r).last().unwrap(), expected);
    assert_eq!(r.files.read("manual-overrides.json").unwrap(), saved);
    let state = r.port.state.lock().unwrap();
    assert_eq!(state.writes, writes + 2);
    assert_eq!(state.opened, state.retired);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_correction_saved_pending_profile_edit_and_temporary_expiry_keep_cleanup_provenance() {
    for kind in 0..3 {
        let r = fan_rig(0, 0);
        let profile = |rpm: u16| {
            let mut p = r.files.document("profiles.json");
            let mut fan = crate::installed::readiness::thermal_storage::legacy_fan(1);
            fan["fixedSpeedRpm"] = json!(rpm);
            p["profiles"]["pluggedIn"]["fanControl"] = fan;
            p["profiles"]["pluggedIn"]["fans"] = json!(1);
            r.files.put("profiles.json", p);
            let mut c = r.files.document("battery-configuration.commit.json");
            c["profileSha256"] = json!(crate::configuration::digest_hex(
                &r.files.read("profiles.json").unwrap().unwrap()
            ));
            r.files.put("battery-configuration.commit.json", c);
        };
        if kind == 1 {
            profile(3700);
        }
        r.get().await;
        let mut request = fan_request(200, if kind == 1 { None } else { Some(1) }, 3700);
        if kind == 2 {
            request["value"]["lifetime"] = json!(0);
        }
        assert_eq!(r.request(request).await["result"]["status"], 1);
        let owner = fan_rows(&r).last().unwrap().clone();
        if kind == 0 {
            assert_eq!(
                r.request(fan_request(201, Some(2), 4000)).await["result"]["status"],
                2
            );
        } else if kind == 1 {
            profile(4000);
        } else {
            let _ = saved_fan(&r.storage, 2).unwrap();
            assert!(r.storage.current_fan_override(0).unwrap().is_none());
        }
        let saved = r.files.read("manual-overrides.json").unwrap();
        let writes = r.port.state.lock().unwrap().writes;
        r.origin.stop();
        r.stop().await;
        let mut expected = owner;
        expected["progress"] = json!(50);
        assert_eq!(*fan_rows(&r).last().unwrap(), expected);
        assert_eq!(r.files.read("manual-overrides.json").unwrap(), saved);
        assert_eq!(r.port.state.lock().unwrap().writes, writes + 2);
    }
}
#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_correction_fresh_scope_and_stale_implicit_stop_remain_fenced() {
    for reason in 0..6 {
        let r = fan_rig(0, 0);
        r.get().await;
        r.request(fan_request(210, Some(1), 3700)).await;
        let before = r.files.read("thermal-mutation.journal").unwrap();
        let saved = r.files.read("manual-overrides.json").unwrap();
        let writes = r.port.state.lock().unwrap().writes;
        r.custody.lifecycle.ingress(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_SESSIONCHANGE,
            0,
        );
        match reason {
            0 => {
                r.custody.authentication.store(2, Ordering::Release);
            }
            1 => {
                r.custody.generation.store(8, Ordering::Release);
            }
            2 => r.origin.lose_trust(),
            3 => r.custody.integrity_lost.store(true, Ordering::Release),
            5 => {
                let mut v = r.files.document("manual-overrides.json");
                v["overrides"]["fans"] = json!(3);
                r.files.put("manual-overrides.json", v);
            }
            _ => {}
        }
        if reason < 4 {
            let request = crate::service_status::StatusRequest::decode(
                &serde_json::to_vec(&fan_request(211, Some(0), 3700)).unwrap(),
            )
            .unwrap();
            let result = r
                .source
                .lock()
                .await
                .mutate(
                    USER,
                    SID.into(),
                    request,
                    Instant::now() + Duration::from_secs(3),
                )
                .await;
            assert!(result.is_err() || result.unwrap().0.status == 2);
        }
        let saved = if reason == 5 {
            r.files.read("manual-overrides.json").unwrap()
        } else {
            saved
        };
        r.origin.stop();
        assert!(r.source.lock().await.drain().await.is_err());
        assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), before);
        assert_eq!(r.files.read("manual-overrides.json").unwrap(), saved);
        assert_eq!(r.port.state.lock().unwrap().writes, writes);
    }
    let r = fan_rig(0, 0);
    r.get().await;
    r.files.fail.store(true, Ordering::Release);
    assert_eq!(
        r.request(fan_request(212, Some(1), 3700)).await["result"]["status"],
        2
    );
    assert!(fan_rows(&r).is_empty());
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_correction_cancelled_auto_terminal_does_not_obstruct_later_true_noop() {
    for already_auto in [false, true] {
        let r = fan_rig(2, 0);
        r.get().await;
        r.request(fan_request(220, Some(1), 3700)).await;
        if already_auto {
            r.port.state.lock().unwrap().fans = [0; 2];
        }
        let entered = Arc::new(AtomicBool::new(false));
        let release = Arc::new(AtomicBool::new(false));
        let hook_entered = entered.clone();
        let hook_release = release.clone();
        r.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
            hook_entered.store(true, Ordering::Release);
            let deadline = std::time::Instant::now() + HANG_GUARD;
            while !hook_release.load(Ordering::Acquire) {
                assert!(std::time::Instant::now() < deadline);
                std::thread::sleep(Duration::from_millis(2));
            }
        }));
        let request = crate::service_status::StatusRequest::decode(
            &serde_json::to_vec(&fan_request(221, Some(0), 3700)).unwrap(),
        )
        .unwrap();
        let mut future = r.source.lock().await.mutate(
            USER,
            SID.into(),
            request,
            Instant::now() + Duration::from_secs(3),
        );
        let deadline = Instant::now() + HANG_GUARD;
        while !entered.load(Ordering::Acquire) {
            assert!(Instant::now() < deadline);
            tokio::select! { result=&mut future => panic!("retirement hold ended early: {}", result.is_ok()), _=tokio::time::sleep(Duration::from_millis(5))=>{} }
        }
        assert_eq!(
            fan_rows(&r).last().unwrap()["progress"],
            if already_auto { 51 } else { 50 }
        );
        drop(future);
        r.port.state.lock().unwrap().on_mutation_retired = None;
        release.store(true, Ordering::Release);
        let bytes = r.files.read("thermal-mutation.journal").unwrap();
        let writes = r.port.state.lock().unwrap().writes;
        assert_eq!(
            r.request(fan_request(222, Some(0), 3700)).await["result"]["status"],
            1
        );
        r.origin.stop();
        r.stop().await;
        assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), bytes);
        let state = r.port.state.lock().unwrap();
        assert_eq!(state.writes, writes);
        assert_eq!(state.opened, state.retired);
    }
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_correction_cancelled_registered_preflight_keeps_scope_until_native_join() {
    let r = fan_rig(0, 0);
    r.get().await;
    let held = Arc::new(AtomicBool::new(false));
    let opened = r.port.state.lock().unwrap().opened;
    r.port.state.lock().unwrap().held_read = Some(held.clone());
    let request = crate::service_status::StatusRequest::decode(
        &serde_json::to_vec(&fan_request(230, Some(1), 3700)).unwrap(),
    )
    .unwrap();
    let mut future = r.source.lock().await.mutate(
        USER,
        SID.into(),
        request,
        Instant::now() + Duration::from_secs(3),
    );
    let deadline = Instant::now() + HANG_GUARD;
    while r.port.state.lock().unwrap().opened == opened {
        assert!(Instant::now() < deadline);
        tokio::select! { result=&mut future => panic!("held preflight ended early: {}", result.is_ok()), _=tokio::time::sleep(Duration::from_millis(5))=>{} }
    }
    drop(future);
    assert!(fan_rows(&r).is_empty());
    let captures = r.factory_calls.load(Ordering::Acquire);
    let foreign = json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000231","value":value(1, 1)});
    assert_eq!(
        r.request_as(foreign, USER, "S-1-5-21-111-222-333-1002")
            .await["result"]["status"],
        2
    );
    assert_eq!(r.factory_calls.load(Ordering::Acquire), captures);
    held.store(true, Ordering::Release);
    assert_eq!(
        r.request(fan_request(232, Some(0), 3700)).await["result"]["status"],
        1
    );
    assert!(fan_rows(&r).is_empty());
    r.origin.stop();
    r.stop().await;
    let state = r.port.state.lock().unwrap();
    assert_eq!(state.writes, 0);
    assert_eq!(state.opened, state.retired);
}

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_correction_known_unpublished_activation_does_not_keep_scope_fence() {
    let r = fan_rig(0, 0);
    r.get().await;
    r.custody.activation_refusal.store(1, Ordering::Release);
    assert_eq!(
        r.request(fan_request(240, Some(1), 3700)).await["result"]["status"],
        2
    );
    let captures = r.factory_calls.load(Ordering::Acquire);
    let foreign = json!({"operation":"setPerformanceOverride","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000241","value":value(1, 1)});
    assert_eq!(
        r.request_as(foreign, USER, "S-1-5-21-111-222-333-1002")
            .await["result"]["status"],
        2
    );
    assert_eq!(r.factory_calls.load(Ordering::Acquire), captures + 1);
    assert!(fan_rows(&r).is_empty());
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
    r.origin.stop();
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
fn battery_request(n: u32, operation: &str, value: Value) -> Value {
    let mut r = json!({"operation":operation,"version":91,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}")});
    r[if operation == "setBatteryPolicy" {
        "value"
    } else {
        "enabled"
    }] = value;
    r
}
#[cfg(feature = "service-battery-policy-v1")]
#[test]
fn battery_v1_strict_existing_requests_no_raw_or_lifetime_escape() {
    use crate::service_status::StatusRequest;
    let valid = battery_request(1, "setBatteryPolicy", json!({"chargeLimitPercent":70}));
    assert!(StatusRequest::decode(&serde_json::to_vec(&valid).unwrap()).is_ok());
    for value in [
        json!({"chargeLimitPercent":49}),
        json!({"chargeLimitPercent":100}),
        json!({"chargeLimitPercent":70,"lifetime":1}),
        Value::Null,
    ] {
        let mut v = valid.clone();
        v["value"] = value;
        assert!(StatusRequest::decode(&serde_json::to_vec(&v).unwrap()).is_err());
    }
    for (k, v) in [
        ("lifetime", json!(1)),
        ("version", json!(80)),
        ("version", json!(PROTOCOL_VERSION - 1)),
        ("raw", json!(198)),
        ("requestId", json!("bad")),
    ] {
        let mut r = valid.clone();
        r[k] = v;
        assert!(StatusRequest::decode(&serde_json::to_vec(&r).unwrap()).is_err());
    }
    let duplicate = serde_json::to_string(&valid).unwrap().replace(
        "\"chargeLimitPercent\":70",
        "\"chargeLimitPercent\":70,\"chargeLimitPercent\":65",
    );
    assert!(StatusRequest::decode(duplicate.as_bytes()).is_err());
    for op in ["setBatteryProtection", "setFullChargeOverride"] {
        for value in [true, false] {
            assert!(
                StatusRequest::decode(
                    &serde_json::to_vec(&battery_request(2, op, json!(value))).unwrap()
                )
                .is_ok()
            );
        }
    }
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_direct_off_limit_and_noop_preserve_configuration_and_separate_reads() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let r = Rig::new(files);
    r.get().await;
    let original = r.files.read("profiles.json").unwrap();
    for (n, op, value, target, writes) in [
        (
            1,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
            0xc6,
            1,
        ),
        (2, "setBatteryProtection", json!(false), 0x50, 2),
        (
            3,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":65}),
            0x50,
            2,
        ),
        (4, "setBatteryProtection", json!(true), 0xc1, 3),
        (5, "setBatteryProtection", json!(true), 0xc1, 3),
    ] {
        let result = r.request(battery_request(n, op, value)).await;
        assert_eq!(result["result"]["status"], 1, "{result}");
        assert_eq!(r.port.state.lock().unwrap().battery_raw, target);
        assert_eq!(r.port.state.lock().unwrap().writes, writes);
        let current = r.get().await;
        assert_eq!(
            current["status"]["confirmedState"]["battery"]["protectionEnabled"],
            target != 0x50
        );
    }
    let status = r.get().await;
    assert_eq!(
        status["status"]["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        65
    );
    assert_eq!(r.files.read("profiles.json").unwrap(), original);
    assert!(r.files.read("manual-overrides.json").unwrap().is_none());
    let rows = fan_rows(&r);
    assert_eq!(rows.last().unwrap()["progress"], 63);
    {
        let s = r.port.state.lock().unwrap();
        assert_eq!(s.sent.iter().filter(|v| v.0 == 7 && v.1 == 0x12).count(), 3);
        assert_eq!(s.sent.iter().filter(|v| v.0 == 7 && v.1 == 0x8f).count(), 5);
        assert_eq!(
            s.sent
                .iter()
                .map(|v| v.3)
                .collect::<std::collections::BTreeSet<_>>()
                .len(),
            s.sent.len()
        );
    }
    r.stop().await;
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_unknown_temporary_and_active_do_not_mint_authority_or_infer_false() {
    for raw in [4, 0x80] {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        r.port.state.lock().unwrap().temporary_raw = raw;
        let result = r
            .request(battery_request(1, "setBatteryProtection", json!(false)))
            .await;
        assert_eq!(result["result"]["status"], 2);
        assert_eq!(r.port.state.lock().unwrap().writes, 0);
        assert!(fan_rows(&r).is_empty());
        let status = r.get().await;
        assert_eq!(
            status["status"]["confirmedState"]["battery"]["temporaryFullCharge"],
            if raw == 4 { json!(true) } else { Value::Null }
        );
        let result = r
            .request(battery_request(2, "setFullChargeOverride", json!(false)))
            .await;
        if cfg!(feature = "service-temporary-charge-v1") && raw == 4 {
            assert_eq!(result["result"]["status"], 1, "{result}");
            assert_eq!(r.port.state.lock().unwrap().writes, 1);
        } else {
            assert_eq!(result["result"]["status"], 2, "{result}");
            if !cfg!(feature = "service-temporary-charge-v1") {
                assert!(
                    result["result"]["message"]
                        .as_str()
                        .unwrap()
                        .contains("integration is pending")
                );
            }
            assert_eq!(r.port.state.lock().unwrap().writes, 0);
        }
        r.stop().await;
    }
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_only_exact_stale_baseline_waits_nine_reads_with_one_setter() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.port.state.lock().unwrap().battery_replies = std::iter::repeat_n(0xd0, 8).collect();
    let result = r
        .request(battery_request(
            1,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
        ))
        .await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    {
        let s = r.port.state.lock().unwrap();
        assert_eq!(s.battery_reads, 10);
        assert_eq!(s.writes, 1);
        let times: Vec<_> = s
            .sent
            .iter()
            .zip(&s.sent_at)
            .filter(|(v, _)| v.0 == 7 && v.1 == 0x92)
            .map(|(_, t)| *t)
            .collect();
        for pair in times[1..].windows(2) {
            assert!(pair[1] - pair[0] >= Duration::from_millis(250));
        }
    }
    r.stop().await;
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_failed_ack_other_invalid_failed_or_exhausted_reads_fence_without_replay() {
    for case in 0..5 {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        {
            let mut s = r.port.state.lock().unwrap();
            match case {
                0 => s.battery_bad_ack = true,
                1 => s.battery_replies.push_back(0xc1),
                2 => s.battery_bad_read = true,
                3 => s.battery_fail_read = true,
                _ => s.battery_replies = std::iter::repeat_n(0xd0, 9).collect(),
            }
        }
        let result = r
            .request(battery_request(
                1,
                "setBatteryPolicy",
                json!({"chargeLimitPercent":70}),
            ))
            .await;
        assert_eq!(result["result"]["status"], 2, "{case} {result}");
        assert!(r.storage.pending());
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        let result = r
            .request(battery_request(2, "setBatteryProtection", json!(false)))
            .await;
        assert_eq!(result["result"]["status"], 2);
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        assert!(
            !crate::thermal_history::rows(
                &r.files.read("thermal-mutation.journal").unwrap().unwrap(),
                |_, _| Ok(())
            )
            .unwrap()
            .1
            .is_some_and(|e| e.progress == 62)
        );
        r.stop().await;
    }
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_positive_fan_coexists_then_original_reserved_release_on_stop() {
    let r = fan_rig(0, 0);
    r.get().await;
    let result = r.request(fan_request(1, Some(1), 3700)).await;
    assert_eq!(result["result"]["status"], 1);
    let owner = fan_rows(&r).last().unwrap().clone();
    let before = r.files.read("manual-overrides.json").unwrap();
    let writes = r.port.state.lock().unwrap().writes;
    let result = r
        .request(battery_request(2, "setBatteryProtection", json!(false)))
        .await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(r.port.state.lock().unwrap().writes, writes + 1);
    assert!(r.storage.outstanding_fan());
    assert_eq!(r.files.read("manual-overrides.json").unwrap(), before);
    let battery = fan_rows(&r).last().unwrap().clone();
    assert!(battery["battery"]["fan_owner"].is_string());
    assert_eq!(battery["progress"], 62);
    r.stop().await;
    let rows = fan_rows(&r);
    let last = rows.last().unwrap();
    assert_eq!(last["progress"], 50);
    assert_eq!(last["request"], owner["request"]);
    assert_eq!(last["fan"], owner["fan"]);
    assert!(!r.storage.outstanding_fan());
    let s = r.port.state.lock().unwrap();
    assert_eq!(
        s.sent
            .iter()
            .map(|v| v.3)
            .collect::<std::collections::BTreeSet<_>>()
            .len(),
        s.sent.len()
    );
    assert_eq!(s.sent.iter().filter(|v| v.0 == 7 && v.1 == 0x12).count(), 1);
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_prepublication_budget_and_durable_cuts_keep_fences_distinct() {
    for case in 0..5 {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        match case {
            0 => r.port.reserve_for_tests(121),
            1 => r.files.capacity_refuse.store(true, Ordering::Release),
            2 => r.files.reserve_uncertain.store(true, Ordering::Release),
            3 => r.files.fail_prepared.store(3, Ordering::Release),
            _ => r.files.fail_first_intent.store(3, Ordering::Release),
        };
        let result = r
            .request(battery_request(1, "setBatteryProtection", json!(false)))
            .await;
        assert_eq!(result["result"]["status"], 2, "{case} {result}");
        assert_eq!(r.port.state.lock().unwrap().writes, 0);
        assert_eq!(r.storage.pending(), case >= 2);
        r.stop().await;
    }
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_battery_source_and_other_temporary_bits_are_admitted() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    {
        let mut s = r.port.state.lock().unwrap();
        s.actual = 0;
        s.temporary_raw = 0x0b;
    }
    let result = r
        .request(battery_request(
            1,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":60}),
        ))
        .await;
    assert_eq!(result["result"]["status"], 1, "{result}");
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    assert_eq!(fan_rows(&r).last().unwrap()["battery"]["source"], 1);
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_fresh_power_controller_owner_changes_never_submit() {
    for change in 0..4 {
        let r = Rig::new(MemoryFiles::default());
        if change == 3 {
            r.files.profiles(1);
        }
        r.get().await;
        if change == 0 {
            r.port.state.lock().unwrap().change_power = Some((2, 0));
        } else {
            let c = r.custody.clone();
            let files = r.files.clone();
            r.port.state.lock().unwrap().on_power = Some(Arc::new(move |n| {
                if n == 2 {
                    if change == 1 {
                        c.fresh_refuse.store(true, Ordering::Release);
                    } else if change == 2 {
                        c.generation.store(8, Ordering::Release);
                    } else {
                        let mut profile = files.document("profiles.json");
                        profile["profiles"]["pluggedIn"]["batteryProtectionEnabled"] = json!(false);
                        files.put("profiles.json", profile);
                        let mut commit = files.document("battery-configuration.commit.json");
                        commit["profileSha256"] = json!(crate::configuration::digest_hex(
                            &files.read("profiles.json").unwrap().unwrap()
                        ));
                        files.put("battery-configuration.commit.json", commit);
                    }
                }
            }));
        }
        let result = r
            .request(battery_request(1, "setBatteryProtection", json!(false)))
            .await;
        assert_eq!(result["result"]["status"], 2, "{change} {result}");
        assert_eq!(r.port.state.lock().unwrap().writes, 0);
        let rows = fan_rows(&r);
        assert!(rows.last().is_none_or(|v| v["progress"] == 6));
        r.stop().await;
    }
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let result = r
        .request_as(
            battery_request(2, "setBatteryProtection", json!(false)),
            Caller {
                session: 8,
                control_session: Some(8),
                ..USER
            },
            SID,
        )
        .await;
    assert_eq!(result["result"]["status"], 2);
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
    r.stop().await;
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_known_capacity_refusal_retains_original_fan_release() {
    for journal in [false, true] {
        let r = fan_rig(0, 0);
        r.get().await;
        assert_eq!(
            r.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
            1
        );
        let owner = fan_rows(&r).last().unwrap().clone();
        let writes = r.port.state.lock().unwrap().writes;
        if journal {
            r.files.capacity_refuse.store(true, Ordering::Release);
        } else {
            r.port.exhaust();
        }
        assert_eq!(
            r.request(battery_request(2, "setBatteryProtection", json!(false)))
                .await["result"]["status"],
            2
        );
        assert_eq!(r.port.state.lock().unwrap().writes, writes);
        assert!(r.storage.outstanding_fan());
        assert!(!r.storage.pending());
        r.stop().await;
        let rows = fan_rows(&r);
        assert_eq!(rows.last().unwrap()["progress"], 50);
        assert_eq!(rows.last().unwrap()["fan"], owner["fan"]);
    }
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_v1_canceled_possible_submission_keeps_global_fence_and_original_fan() {
    let r = fan_rig(0, 0);
    r.get().await;
    assert_eq!(
        r.request(fan_request(1, Some(1), 3700)).await["result"]["status"],
        1
    );
    let writes = r.port.state.lock().unwrap().writes;
    let held = Arc::new(AtomicBool::new(false));
    r.port.state.lock().unwrap().held_write = Some((writes + 1, held.clone()));
    {
        let request = r.request(battery_request(2, "setBatteryProtection", json!(false)));
        tokio::pin!(request);
        tokio::select! {v=&mut request=>panic!("held request completed {v}"),_=async{while r.port.state.lock().unwrap().writes==writes{tokio::time::sleep(Duration::from_millis(5)).await;}}=>{}}
    }
    assert!(r.storage.pending());
    assert!(r.storage.outstanding_fan());
    {
        let s = r.port.state.lock().unwrap();
        assert!(s.opened > s.retired);
    }
    held.store(true, Ordering::Release);
    r.reader.drain_active().await.unwrap();
    let result = r
        .request(battery_request(
            3,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":60}),
        ))
        .await;
    assert_eq!(result["result"]["status"], 2);
    assert_eq!(r.port.state.lock().unwrap().writes, writes + 1);
    assert!(r.storage.outstanding_fan());
    assert!(r.storage.pending());
    // Normal Stop must not submit a fan alternative through unresolved battery output.
    let result = r.source.lock().await.drain().await;
    assert!(result.is_err());
    assert_eq!(r.port.state.lock().unwrap().writes, writes + 1);
    assert!(r.storage.outstanding_fan());
}

// Corrective-stage regressions: all use the actual dispatcher/Reader with memory suppliers.
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_saved_limits_allow_direct_off_and_supported_replacement() {
    for saved in [85, 90] {
        for protection in [true, false] {
            for replacement in [false, true] {
                let files = MemoryFiles::default();
                files.profiles(1);
                let mut profile = files.document("profiles.json");
                profile["profiles"]["batteryPolicy"]["chargeLimitPercent"] = json!(saved);
                profile["profiles"]["pluggedIn"]["batteryProtectionEnabled"] = json!(protection);
                files.put("profiles.json", profile);
                let mut commit = files.document("battery-configuration.commit.json");
                commit["profileSha256"] = json!(crate::configuration::digest_hex(
                    &files.read("profiles.json").unwrap().unwrap()
                ));
                files.put("battery-configuration.commit.json", commit);
                let r = Rig::new(files);
                r.get().await;
                let original = r.files.read("profiles.json").unwrap();
                let request = if replacement {
                    battery_request(301, "setBatteryPolicy", json!({"chargeLimitPercent":70}))
                } else {
                    battery_request(301, "setBatteryProtection", json!(false))
                };
                let result = r.request(request).await;
                assert_eq!(
                    result["result"]["status"], 1,
                    "saved={saved} protection={protection} replacement={replacement}: {result}"
                );
                let desired = r.storage.desired_battery(0).unwrap();
                assert_eq!(desired.limit, if replacement { 70 } else { saved });
                assert_eq!(desired.protection, replacement && protection);
                assert_eq!(
                    r.port.state.lock().unwrap().battery_raw,
                    if replacement && protection {
                        0xc6
                    } else {
                        0x50
                    }
                );
                assert_eq!(r.files.read("profiles.json").unwrap(), original);
                let status = r.get().await;
                assert_eq!(
                    status["status"]["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
                    desired.limit
                );
                assert_eq!(
                    status["status"]["confirmedState"]["battery"]["chargeLimitPercent"],
                    if desired.protection {
                        json!(70)
                    } else {
                        Value::Null
                    }
                );
                let writes = r.port.state.lock().unwrap().writes;
                let rejected = r
                    .request(battery_request(
                        302,
                        "setBatteryPolicy",
                        json!({"chargeLimitPercent":saved}),
                    ))
                    .await;
                assert_eq!(rejected["result"]["status"], 2);
                assert_eq!(r.port.state.lock().unwrap().writes, writes);
                assert_eq!(r.storage.desired_battery(0).unwrap(), desired);
                if !replacement {
                    assert_eq!(
                        r.request(battery_request(303, "setBatteryProtection", json!(true)))
                            .await["result"]["status"],
                        2
                    );
                    assert_eq!(r.port.state.lock().unwrap().writes, writes);
                }
                r.stop().await;
            }
        }
    }
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_pending_history_expires_without_hiding_shared_fence() {
    let r = fan_rig(0, 0);
    r.get().await;
    assert_eq!(
        r.request(fan_request(310, Some(1), 3700)).await["result"]["status"],
        1
    );
    r.port.state.lock().unwrap().battery_bad_ack = true;
    assert_eq!(
        r.request(battery_request(311, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        2
    );
    let reason = r.storage.pending_reason().unwrap();
    let status = r.get().await;
    assert!(
        status["status"]["message"]
            .as_str()
            .unwrap()
            .contains(reason),
        "{status}"
    );
    assert_eq!(
        status["status"]["confirmedState"]["battery"]["chargeLimitPercent"],
        80
    );
    assert!(r.storage.outstanding_fan());
    let writes = r.port.state.lock().unwrap().writes;
    tokio::time::pause();
    tokio::time::advance(Duration::from_secs(6)).await;
    let later = r.get().await;
    assert!(
        later["status"]["confirmedState"]["battery"].is_null(),
        "expired battery history reintroduced: {later}"
    );
    assert!(
        later["status"]["message"]
            .as_str()
            .unwrap()
            .contains(reason)
    );
    assert_eq!(r.port.state.lock().unwrap().writes, writes);
    assert!(r.storage.pending());
    assert!(r.source.lock().await.drain().await.is_err());
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_recoverable_profile_failure_keeps_status_and_fatal_class() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(320, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        1
    );
    r.files.put(
        "profiles.json",
        json!({"schemaVersion":crate::configuration::CURRENT_SCHEMA}),
    );
    let result = r.source.lock().await.snapshot(USER, Instant::now()).await;
    assert!(
        result.is_ok(),
        "recoverable aggregate failure escaped: {:?}",
        result.err()
    );
    let value = serde_json::to_value(result.unwrap()).unwrap();
    assert_eq!(
        value["confirmedState"]["battery"]["protectionEnabled"],
        false
    );
    assert_eq!(value["writesBlocked"], true);
    r.custody.integrity_lost.store(true, Ordering::Release);
    let error = r
        .source
        .lock()
        .await
        .snapshot(USER, Instant::now())
        .await
        .err()
        .unwrap();
    assert!(crate::installed::readiness::thermal_mutation::is_fatal_admission(&error));
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_first_committed_intent_survives_final_authority_loss() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let custody = r.custody.clone();
    r.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        custody.fresh_refuse.store(true, Ordering::Release)
    }));
    let result = r
        .request(battery_request(
            330,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
        ))
        .await;
    assert_eq!(result["result"]["status"], 2);
    assert_eq!(fan_rows(&r).last().unwrap()["progress"], 62);
    assert_eq!(r.storage.desired_battery(0).unwrap().limit, 70);
    r.custody.fresh_refuse.store(false, Ordering::Release);
    let status = r.get().await;
    assert_eq!(
        status["status"]["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"], 70,
        "{status}"
    );
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    let foreign = r.request_as(json!({"operation":"getStatus","version":91,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000331"}), USER, "S-1-5-21-111-222-333-1002").await;
    assert_ne!(
        foreign["status"]["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        70
    );
    r.port.state.lock().unwrap().on_mutation_retired = None;
    r.stop().await;
    let reopened = Storage::open(Box::new(r.files.clone()), SID).unwrap();
    assert_eq!(
        reopened.desired_battery(0).unwrap().limit,
        80,
        "process override must not become durable configuration"
    );
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_queued_result_preserves_latest_thermal_metadata_and_witness() {
    for expire in [false, true] {
        let r = Rig::new(MemoryFiles::default());
        fresh_observe(&r).await;
        let release = Arc::new(AtomicBool::new(false));
        let holding = {
            let mut s = r.port.state.lock().unwrap();
            s.held_write = Some((1, release.clone()));
            s.holding.clone()
        };
        let a = r.set(240, value(1, 1));
        tokio::pin!(a);
        tokio::select! {_=&mut a=>panic!("held performance finished"),v=tokio::time::timeout(RetirementGate::LIVENESS,holding.notified())=>v.unwrap()}
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        let request = crate::service_status::StatusRequest::decode(
            &serde_json::to_vec(&battery_request(341, "setBatteryProtection", json!(false)))
                .unwrap(),
        )
        .unwrap();
        let mut b = r.source.lock().await.mutate(
            USER,
            SID.into(),
            request,
            Instant::now() + Duration::from_secs(30),
        );
        std::future::poll_fn(|cx| {
            assert!(b.as_mut().poll(cx).is_pending());
            Poll::Ready(())
        })
        .await;
        release.store(true, Ordering::Release);
        assert_eq!(a.await["result"]["status"], 1);
        if expire {
            r.reader.expire_for_tests();
            r.metadata.store(1, Ordering::Release);
            tokio::time::pause();
            tokio::time::advance(Duration::from_secs(2)).await;
            r.source.lock().await.refresh_publications().await.unwrap();
            tokio::time::resume();
        }
        let before = r
            .source
            .lock()
            .await
            .publications()
            .unwrap()
            .borrow()
            .active
            .clone();
        let witness = r.reader.last_completed_for_tests();
        let (outcome, published) = b.await.unwrap();
        assert_eq!(outcome.status, 1);
        let after = serde_json::to_value(published.unwrap()).unwrap();
        let before = serde_json::to_value(before).unwrap();
        assert_eq!(
            after["confirmedState"]["performance"], before["confirmedState"]["performance"],
            "{after}"
        );
        assert_eq!(after["activePowerSource"], before["activePowerSource"]);
        if expire {
            assert!(
                after["confirmedState"].get("performance").is_none()
                    || after["confirmedState"]["performance"].is_null()
            );
        }
        assert_eq!(after["performanceOverride"], before["performanceOverride"]);
        assert_eq!(
            after["desiredState"]["performance"],
            before["desiredState"]["performance"]
        );
        assert!(after["revision"].as_u64().unwrap() > before["revision"].as_u64().unwrap());
        assert_eq!(r.reader.last_completed_for_tests(), witness);
        assert_eq!(r.port.state.lock().unwrap().writes, 3);
        r.stop().await;
    }
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_both_decorator_slots_preserve_fatal_and_recoverable_contract() {
    for slot in [0, 1] {
        for fatal in [false, true] {
            let r = Rig::new(MemoryFiles::default());
            r.get().await;
            let base = r
                .source
                .lock()
                .await
                .publications()
                .unwrap()
                .borrow()
                .active
                .clone();
            let callback: Rc<dyn Fn(Snapshot) -> io::Result<Snapshot>> = Rc::new(move |_| {
                if fatal {
                    Err(
                        crate::installed::readiness::thermal_mutation::fatal_admission(
                            "injected decorator custody loss",
                        ),
                    )
                } else {
                    Err(io::ErrorKind::Unsupported.into())
                }
            });
            if slot == 0 {
                r.reader.retain_policy(callback);
            } else {
                r.reader.retain_battery_policy(callback);
            }
            let result = r.reader.current_policy(base);
            if fatal {
                assert!(
                    crate::installed::readiness::thermal_mutation::is_fatal_admission(
                        &result.err().unwrap()
                    )
                );
            } else {
                let result = serde_json::to_value(result.unwrap()).unwrap();
                assert_eq!(result["writesBlocked"], true);
                assert!(
                    result["message"]
                        .as_str()
                        .unwrap()
                        .contains("private policy is unavailable")
                );
            }
            r.stop().await;
        }
    }
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_sibling_publication_does_not_extend_battery_history() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(
            350,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70})
        ))
        .await["result"]["status"],
        1
    );
    tokio::time::sleep(Duration::from_millis(4000)).await;
    assert_eq!(r.set(245, value(1, 1)).await["result"]["status"], 1);
    tokio::time::sleep(Duration::from_millis(1100)).await;
    let status = r.get().await;
    assert_eq!(status["status"]["confirmedState"]["performance"], 1);
    assert!(
        status["status"]["confirmedState"]["battery"].is_null(),
        "sibling thermal timestamp extended battery history: {status}"
    );
    assert_eq!(
        status["status"]["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        70
    );
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_correction_preflight_refusal_preserves_original_bounded_history() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(
            360,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70})
        ))
        .await["result"]["status"],
        1
    );
    r.port.exhaust();
    assert_eq!(
        r.request(battery_request(361, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        2
    );
    let status = r.get().await;
    assert_eq!(
        status["status"]["confirmedState"]["battery"]["chargeLimitPercent"], 70,
        "{status}"
    );
    assert_eq!(
        status["status"]["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        70
    );
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_replaces_contribution_for_existing_receiver() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(401, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        1
    );
    r.get().await;
    let mut receiver = r.source.lock().await.publications().unwrap();
    receiver.borrow_and_update();
    assert_eq!(
        r.request(battery_request(
            402,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70})
        ))
        .await["result"]["status"],
        1
    );
    assert!(receiver.has_changed().unwrap());
    let active = serde_json::to_value(&receiver.borrow_and_update().active).unwrap();
    let message = active["message"].as_str().unwrap();
    assert!(
        message.contains("confirmed by independent readback"),
        "{active}"
    );
    assert!(
        !message.contains("Last independently read battery policy retained"),
        "{active}"
    );
    assert!(
        !message.contains("Last battery observation expired"),
        "{active}"
    );
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_contribution_replaces_only_battery_reason() {
    let base = Snapshot::observed_read_only(
        1,
        0,
        false,
        false,
        vec![],
        None,
        "Unrelated source, fan cleanup and unresolved ownership reason.".into(),
    );
    let old = base
        .unavailable_mailbox(1, " Thermal observation expired.")
        .battery_projection(
            None,
            Some((0xd0, Some(false))),
            "Old battery pending-only contribution.",
        );
    let next = old.without_battery_observation().battery_projection(
        None,
        Some((0xc6, Some(false))),
        "New battery confirmation.",
    );
    let wire = serde_json::to_value(next).unwrap();
    let message = wire["message"].as_str().unwrap();
    assert!(
        message.contains("Unrelated source, fan cleanup and unresolved ownership reason."),
        "{wire}"
    );
    assert!(message.contains("Thermal observation expired."), "{wire}");
    assert!(message.contains("New battery confirmation."), "{wire}");
    assert!(
        !message.contains("Old battery pending-only contribution."),
        "{wire}"
    );
    assert!(wire.get("diagnostics").is_none());
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_recoverable_private_boundary_keeps_public_provenance() {
    for nested in [false, true] {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        r.reader.retain_policy(Rc::new(|base| {
            Ok(base
                .thermal_intent(None, None, "SYNTHETIC_PRIVATE_SENTINEL", None)
                .bind_private_user(SID))
        }));
        r.reader.retain_battery_policy(Rc::new(move |base| {
            if nested {
                crate::installed::readiness::mailbox_read::project_private_policy(
                    base.battery_projection(
                        None,
                        Some((0xd0, None)),
                        "Public battery uncertainty.",
                    ),
                    crate::service_status::PrivatePolicyFamily::Battery,
                    |_| Err(io::ErrorKind::Unsupported.into()),
                )
            } else {
                Err(io::ErrorKind::Unsupported.into())
            }
        }));
        let base = Snapshot::observed_read_only(
            1,
            0,
            false,
            false,
            vec![],
            None,
            "Public source uncertainty.".into(),
        );
        let result = r.reader.current_policy(base).unwrap();
        for sid in [None, Some(SID), Some("S-1-5-21-111-222-333-1002")] {
            let wire = serde_json::to_value(result.project_private(sid)).unwrap();
            let message = wire["message"].as_str().unwrap();
            assert!(!message.contains("SYNTHETIC_PRIVATE_SENTINEL"), "{wire}");
            assert!(message.contains("Public source uncertainty."), "{wire}");
            assert!(message.contains("private policy is unavailable"), "{wire}");
            if nested {
                assert!(message.contains("Public battery uncertainty."), "{wire}");
            }
            assert!(wire["performanceOverride"].is_null());
            assert_eq!(wire["writesBlocked"], true);
        }
        r.reader.retain_battery_policy(Rc::new(|_| {
            Err(
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "typed fatal custody",
                ),
            )
        }));
        let base = Snapshot::observed_read_only(1, 0, false, false, vec![], None, String::new());
        assert!(
            crate::installed::readiness::thermal_mutation::is_fatal_admission(
                &r.reader.current_policy(base).err().unwrap()
            )
        );
        r.stop().await;
    }
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_valid_third_read_keeps_pending_without_replay() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.port.state.lock().unwrap().battery_replies.push_back(0xc1);
    let reply = r
        .request(battery_request(
            403,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
        ))
        .await;
    assert_eq!(reply["result"]["status"], 2);
    assert_eq!(r.storage.progress(), Some(61));
    assert!(r.storage.pending());
    let status = r.get().await;
    assert_eq!(
        status["status"]["confirmedState"]["battery"]["chargeLimitPercent"], 65,
        "{status}"
    );
    assert_eq!(
        status["status"]["desiredState"]["battery"]["configuredPolicy"]["chargeLimitPercent"],
        80
    );
    assert!(
        status["status"]["message"]
            .as_str()
            .unwrap()
            .contains("no mutation will be replayed")
    );
    let (writes, reads) = {
        let p = r.port.state.lock().unwrap();
        (p.writes, p.battery_reads)
    };
    assert_eq!((writes, reads), (1, 2));
    let journal = r.files.read("thermal-mutation.journal").unwrap();
    assert_eq!(
        r.request(battery_request(404, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        2
    );
    assert_eq!(r.files.read("thermal-mutation.journal").unwrap(), journal);
    {
        let p = r.port.state.lock().unwrap();
        assert_eq!((p.writes, p.battery_reads), (writes, reads));
    }
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_policy_confirmation_does_not_renew_temporary_age() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.port.state.lock().unwrap().battery_replies = std::iter::repeat_n(0xd0, 8).collect();
    assert_eq!(
        r.request(battery_request(
            405,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70})
        ))
        .await["result"]["status"],
        1
    );
    tokio::time::pause();
    tokio::time::advance(Duration::from_millis(3200)).await;
    let status = r.get().await;
    assert_eq!(
        status["status"]["confirmedState"]["battery"]["chargeLimitPercent"], 70,
        "{status}"
    );
    assert!(
        status["status"]["confirmedState"]["battery"]["temporaryFullCharge"].is_null(),
        "{status}"
    );
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_delayed_temporary_does_not_rejuvenate_policy() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let temporary_started = Arc::new(StdMutex::new(None));
    let started = temporary_started.clone();
    r.port.state.lock().unwrap().battery_getter_hook = Some(Arc::new(move |command, _| {
        if command == 0x8f {
            *started.lock().unwrap() = Some(Instant::now());
            std::thread::sleep(Duration::from_millis(600));
        }
    }));
    // Active temporary charge refuses this basic battery request after it reads
    // policy, then temporary flags, in every battery composition. Temporary-charge
    // builds route setFullChargeOverride to their own flags-only exchange instead.
    r.port.state.lock().unwrap().temporary_raw = 4;
    assert_eq!(
        r.request(battery_request(406, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        2
    );
    tokio::time::pause();
    assert!(
        (r.battery_clock)().unwrap() <= temporary_started.lock().unwrap().unwrap(),
        "policy completion must precede the independently delayed temporary getter"
    );
    tokio::time::advance(Duration::from_millis(4700)).await;
    let status = r.get().await;
    assert!(
        status["status"]["confirmedState"]["battery"].is_null(),
        "{status}"
    );
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
    r.stop().await;
}

/// A rig's Stop after the test paused the clock gives the status observer its
/// drain deadline in real time (`resume_clock_for_stop`): an observer still in
/// one OS call for 50 ms, as a loaded host can leave it, retires inside the
/// drain. On the paused clock the drain reported it running within a few
/// milliseconds.
#[cfg(feature = "service-hyperboost-v1")]
#[tokio::test]
async fn stop_on_a_paused_clock_waits_for_the_observer_in_real_time() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let stall = r.hyperboost.status_stall.clone();
    stall.store(HyperBoostProbes::STALL_THROUGH_CANCEL, Ordering::Release);
    tokio::time::pause();
    // Past the refresh interval, so this status starts a new observation,
    // which its own budget leaves running.
    tokio::time::advance(Duration::from_secs(3)).await;
    r.get().await;
    let release = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(50));
        stall.store(0, Ordering::Release);
    });
    r.stop().await;
    release.join().unwrap();
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_active_subscription_expires_at_unaligned_battery_deadline() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(407, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        1
    );
    let completed = (r.battery_clock)().unwrap();
    let expires = completed + crate::installed::readiness::mailbox_read::RETENTION_WINDOW;
    let original_thermal = r.reader.last_completed_for_tests();
    tokio::time::pause();
    tokio::time::advance(Duration::from_millis(100)).await;
    assert!(
        Instant::now() < expires - Duration::from_secs(2),
        "subscription setup must retain a young observation"
    );
    let subscriptions = Rc::new(Subscriptions::new(2));
    let (mut client, server) = duplex(65536);
    let publications = r.source.lock().await.publications();
    let server = serve_connection(
        Box::new(SidStream {
            inner: test_stream(server, USER, Fault::None),
            sid: SID.into(),
        }),
        r.source.clone(),
        publications,
        subscriptions.clone(),
    );
    let (stop, stopped) = oneshot::channel::<()>();
    let (sent, mut attempts) = mpsc::unbounded_channel();
    let poll = async {
        tokio::select! {
            result=subscriptions.refresh_while_subscribed(||async{
                let due=r.source.lock().await.refresh_publications().await?;
                sent.send((Instant::now(),due)).unwrap();
                Ok(due)
            })=>panic!("refresh stopped {result:?}"),
            _=stopped=>{}
        }
    };
    let exercise = async {
        crate::service_frame::write_payload(&mut client,&serde_json::to_vec(&json!({
            "operation":"subscribeStatus","version":91,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000408"})).unwrap()).await.unwrap();
        let initial = crate::service_frame::read_payload(&mut client)
            .await
            .unwrap();
        let initial: Value = serde_json::from_slice(&initial).unwrap();
        assert_eq!(
            initial["status"]["confirmedState"]["battery"]["protectionEnabled"],
            false
        );
        let (_, mut due) = attempts.recv().await.unwrap();
        while due < expires {
            tokio::time::advance(due - Instant::now()).await;
            let (at, next) = attempts.recv().await.unwrap();
            assert!(at < expires);
            due = next;
        }
        // No GetStatus or manual decorate drives expiry after the initial subscription.
        tokio::time::advance(expires - Duration::from_millis(1) - Instant::now()).await;
        for _ in 0..16 {
            tokio::task::yield_now().await;
        }
        let before = serde_json::to_value(
            &r.source
                .lock()
                .await
                .publications()
                .unwrap()
                .borrow()
                .active,
        )
        .unwrap();
        assert!(!before["confirmedState"]["battery"].is_null(), "{before}");
        // Tokio timers round to a millisecond tick. Bound delivery to that
        // timer granularity; never advance to the next two-second metadata poll.
        let delivered = expires + Duration::from_millis(1);
        tokio::time::advance(delivered - Instant::now()).await;
        for _ in 0..32 {
            tokio::task::yield_now().await;
        }
        let after = serde_json::to_value(
            &r.source
                .lock()
                .await
                .publications()
                .unwrap()
                .borrow()
                .active,
        )
        .unwrap();
        assert!(
            after["confirmedState"]["battery"].is_null(),
            "subscription missed battery expiry without GetStatus: {after}"
        );
        assert_eq!(r.reader.last_completed_for_tests(), original_thermal);
        assert_eq!(
            Instant::now(),
            delivered,
            "test must not wait for the next metadata cadence"
        );
        loop {
            let frame = crate::service_frame::read_payload(&mut client)
                .await
                .unwrap();
            emit_fixture_frame(
                "peer_subscription",
                "subscribeStatus",
                "statusEvent",
                &frame,
            );
            let frame: Value = serde_json::from_slice(&frame).unwrap();
            if frame["status"]["confirmedState"]["battery"].is_null() {
                break;
            }
            assert_eq!(Instant::now(), delivered);
        }
        assert_eq!(Instant::now(), delivered);
        stop.send(()).unwrap();
        drop(client);
    };
    // Each metadata observation runs on a real thread. Without a held clock the
    // idle runtime auto-advances through that thread's 5 ms polls, so a slow
    // host spends the observation budget in virtual time and pushes an attempt
    // past the battery deadline this test advances to.
    let (served, (), ()) =
        hold_clock(&r.port, async { tokio::join!(server, poll, exercise) }).await;
    assert!(served.is_ok());
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_incompatible_independent_fields_are_unavailable_without_guessing() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.port.state.lock().unwrap().temporary_raw = 4;
    // Refused after both independent reads in every battery composition; a
    // temporary-charge build would cancel through setFullChargeOverride instead.
    assert_eq!(
        r.request(battery_request(409, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        2
    );
    {
        let mut port = r.port.state.lock().unwrap();
        port.battery_raw = 0x50;
        port.temporary_raw = 0x80; // Unknown is not independent evidence of false.
    }
    assert_eq!(
        r.request(battery_request(410, "setBatteryProtection", json!(true)))
            .await["result"]["status"],
        2
    );
    let status = r.get().await;
    assert!(
        status["status"]["confirmedState"]["battery"].is_null(),
        "{status}"
    );
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_publication_private_recovery_replaces_only_its_current_failure() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let thermal_failed = Rc::new(std::cell::Cell::new(true));
    let battery_failed = Rc::new(std::cell::Cell::new(false));
    let thermal = thermal_failed.clone();
    r.reader.retain_policy(Rc::new(move |base| {
        if thermal.get() {
            Err(io::ErrorKind::Unsupported.into())
        } else {
            Ok(base
                .thermal_intent(None, None, "Recovered thermal private policy.", None)
                .bind_private_user(SID))
        }
    }));
    let battery = battery_failed.clone();
    r.reader.retain_battery_policy(Rc::new(move |base| {
        if battery.get() {
            Err(io::ErrorKind::Unsupported.into())
        } else {
            Ok(base
                .battery_projection(None, Some((0xd0, None)), "Public battery observation.")
                .bind_private_user(SID))
        }
    }));
    let base = Snapshot::observed_read_only(
        1,
        0,
        false,
        false,
        vec![],
        None,
        "Public source reason.".into(),
    );
    let degraded = r.reader.current_policy(base).unwrap();
    assert!(
        serde_json::to_value(&degraded).unwrap()["message"]
            .as_str()
            .unwrap()
            .contains("private policy is unavailable")
    );
    thermal_failed.set(false);
    let recovered = r.reader.current_policy(degraded).unwrap();
    let wire = serde_json::to_value(&recovered).unwrap();
    assert!(
        !wire["message"]
            .as_str()
            .unwrap()
            .contains("private policy is unavailable"),
        "{wire}"
    );
    thermal_failed.set(true);
    battery_failed.set(true);
    let both = r.reader.current_policy(recovered).unwrap();
    thermal_failed.set(false);
    let battery_only = r.reader.current_policy(both).unwrap();
    assert!(
        serde_json::to_value(&battery_only).unwrap()["message"]
            .as_str()
            .unwrap()
            .contains("private policy is unavailable")
    );
    battery_failed.set(false);
    let recovered = r.reader.current_policy(battery_only).unwrap();
    assert!(
        !serde_json::to_value(recovered).unwrap()["message"]
            .as_str()
            .unwrap()
            .contains("private policy is unavailable")
    );
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_boundary_actual_direct_publication_filters_request_outcome() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(501, "setBatteryProtection", json!(false)))
            .await["result"]["status"],
        1
    );
    let publications = r.source.lock().await.publications().unwrap();
    let latest = publications.borrow().active.clone();
    let owner = serde_json::to_value(latest.project_private(Some(SID))).unwrap();
    assert!(
        owner["message"]
            .as_str()
            .unwrap()
            .contains("confirmed by independent readback")
    );
    for view in [
        latest.project_private(None),
        latest.project_private(Some("S-1-5-21-111-222-333-1002")),
        publications.borrow().inactive.clone(),
    ] {
        let bytes = crate::service_status::encode_status_event(&view).unwrap();
        emit_fixture_frame("publisher_projection", "publication", "statusEvent", &bytes);
        let wire: Value = serde_json::from_slice(&bytes).unwrap();
        let message = wire["status"]["message"].as_str().unwrap();
        assert!(
            !message.contains("confirmed by independent readback"),
            "{wire}"
        );
        assert_eq!(wire["status"]["writesBlocked"], true);
    }
    r.stop().await;
}
#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_boundary_callback_storage_failure_keeps_public_reconciliation_and_fatal_class() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.reader.retain_policy(Rc::new(|base| {
        Ok(base
            .thermal_intent(None, None, "Private thermal text.", None)
            .bind_private_user(SID))
    }));
    r.reader
        .retain_battery_policy(Rc::new(|_| Err(io::ErrorKind::Unsupported.into())));
    let base = Snapshot::observed_read_only(
        1,
        0,
        false,
        false,
        vec![],
        None,
        "Public metadata reason.".into(),
    )
    .battery_projection(None, Some((0xd0, None)), "Public battery observation.");
    let degraded = r.reader.current_policy(base).unwrap();
    let wire = serde_json::to_value(degraded).unwrap();
    let message = wire["message"].as_str().unwrap();
    assert!(message.contains("configuration reconciliation"), "{wire}");
    assert!(!message.contains("to this caller"), "{wire}");
    assert!(message.contains("Public metadata reason."), "{wire}");
    assert!(message.contains("Public battery observation."), "{wire}");
    assert!(!message.contains("Private thermal text."), "{wire}");
    assert_eq!(wire["writesBlocked"], true);
    r.reader.retain_battery_policy(Rc::new(|_| {
        Err(crate::installed::readiness::thermal_mutation::fatal_admission("boundary typed fatal"))
    }));
    let base = Snapshot::observed_read_only(1, 0, false, false, vec![], None, String::new());
    assert!(
        crate::installed::readiness::thermal_mutation::is_fatal_admission(
            &r.reader.current_policy(base).err().unwrap()
        )
    );
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[tokio::test]
async fn battery_pending_once_direct_publication_keeps_fence_and_all_views() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.port.state.lock().unwrap().battery_replies.push_back(0xc1);
    let reply = r
        .request(battery_request(
            601,
            "setBatteryPolicy",
            json!({"chargeLimitPercent":70}),
        ))
        .await;
    assert_eq!(reply["result"]["status"], 2);
    assert_eq!(r.storage.progress(), Some(61));
    assert!(r.storage.pending());
    let reason = r.storage.pending_reason().unwrap();
    let publications = r.source.lock().await.publications().unwrap();
    // Inspect the direct mutation publication before any GetStatus decoration.
    let active = publications.borrow().active.clone();
    for view in [
        active.project_private(Some(SID)),
        active.project_private(None),
        active.project_private(Some("S-1-5-21-111-222-333-1002")),
        publications.borrow().inactive.clone(),
    ] {
        let bytes = crate::service_status::encode_status_event(&view).unwrap();
        emit_fixture_frame("publisher_projection", "publication", "statusEvent", &bytes);
        let wire: Value = serde_json::from_slice(&bytes).unwrap();
        let message = wire["status"]["message"].as_str().unwrap();
        assert_eq!(message.matches(reason).count(), 1, "{wire}");
        assert!(
            message.contains("Matched RZ09-0581 host"),
            "Unrelated source reason lost: {wire}"
        );
        assert_eq!(wire["status"]["writesBlocked"], true);
    }
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    assert_eq!(r.port.state.lock().unwrap().battery_reads, 2);
    assert!(r.storage.pending());
    r.stop().await;
}

#[cfg(feature = "service-battery-policy-v1")]
#[test]
fn battery_pending_once_exact_equality_is_local_after_ownership() {
    let reason = "A thermal command may have been submitted. Read-only reconciliation is required and no mutation will be replayed.";
    // An intentionally identical unrelated source contribution must survive.
    let base = Snapshot::observed_read_only(1, 0, false, false, vec![], None, reason.into())
        .battery_projection(None, None, reason)
        .bind_private_user(SID);
    let same = base.clone().battery_request_outcome(SID, reason);
    let wire = serde_json::to_value(&same).unwrap();
    assert_eq!(
        wire["message"].as_str().unwrap().matches(reason).count(),
        2,
        "{wire}"
    );
    let distinct = base
        .clone()
        .battery_request_outcome(SID, "Distinct owner-only outcome.");
    let owner = serde_json::to_value(distinct.project_private(Some(SID))).unwrap();
    assert!(
        owner["message"]
            .as_str()
            .unwrap()
            .contains("Distinct owner-only outcome.")
    );
    for view in [
        distinct.project_private(None),
        distinct.project_private(Some("S-1-5-21-111-222-333-1002")),
    ] {
        let wire = serde_json::to_value(view).unwrap();
        let message = wire["message"].as_str().unwrap();
        assert_eq!(message.matches(reason).count(), 2, "{wire}");
        assert!(!message.contains("Distinct owner-only outcome."), "{wire}");
    }
    // Whole-string equality only: even a whitespace difference remains distinct.
    let different = base.battery_request_outcome(SID, &format!("{reason} "));
    let wire = serde_json::to_value(different).unwrap();
    assert_eq!(
        wire["message"].as_str().unwrap().matches(reason).count(),
        3,
        "{wire}"
    );
}

#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_actual_dispatch_enable_cancel_noop_and_private_process_intent() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let before = r.files.read("profiles.json").unwrap();
    for (n, enabled, writes) in [
        (701, true, 1),
        (702, true, 1),
        (703, false, 2),
        (704, false, 2),
    ] {
        let response = r
            .request(battery_request(n, "setFullChargeOverride", json!(enabled)))
            .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        assert_eq!(r.port.state.lock().unwrap().writes, writes);
        assert_eq!(r.storage.desired_battery(0).unwrap().temporary, enabled);
    }
    {
        let s = r.port.state.lock().unwrap();
        assert_eq!(
            s.sent
                .iter()
                .filter(|x| x.0 == 7 && x.1 == 0x0f)
                .map(|x| x.2.clone())
                .collect::<Vec<_>>(),
            vec![vec![4], vec![0]]
        );
        assert!(!s.sent.iter().any(|x| x.0 == 7 && x.1 == 0x12));
    }
    assert_eq!(r.files.read("profiles.json").unwrap(), before);
    assert!(r.files.read("manual-overrides.json").unwrap().is_none());
    let restarted = Storage::open(Box::new(r.files.clone()), SID).unwrap();
    assert!(!restarted.desired_battery(0).unwrap().temporary);
    r.stop().await;
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_cancel_ignores_off_or_unknown_policy_on_battery_and_external() {
    for (policy, actual) in [(0x50, 0), (0x7f, 15), (0x7f, 0)] {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        {
            let mut s = r.port.state.lock().unwrap();
            s.battery_raw = policy;
            s.temporary_raw = 4;
            s.actual = actual;
        }
        let response = r
            .request(battery_request(710, "setFullChargeOverride", json!(false)))
            .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        {
            let s = r.port.state.lock().unwrap();
            assert_eq!(s.writes, 1);
            assert_eq!(s.temporary_raw, 0);
            assert!(
                !s.sent
                    .iter()
                    .any(|x| x.0 == 7 && matches!(x.1, 0x92 | 0x12))
            );
        }
        if policy == 0x7f {
            assert!(response["status"]["confirmedState"]["battery"].is_null());
        }
        r.stop().await;
    }
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_full_byte_guard_unknown_flags_and_noop_budget_are_fail_closed() {
    for flags in [1, 2, 8, 16, 128] {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        r.port.state.lock().unwrap().temporary_raw = flags;
        let response = r
            .request(battery_request(720, "setFullChargeOverride", json!(true)))
            .await;
        assert_eq!(response["result"]["status"], 2, "{response}");
        assert_eq!(r.port.state.lock().unwrap().writes, 0);
        r.stop().await;
    }
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    r.port.reserve_for_tests(120);
    let response = r
        .request(battery_request(721, "setFullChargeOverride", json!(true)))
        .await;
    assert_eq!(response["result"]["status"], 2, "{response}");
    assert_eq!(r.port.state.lock().unwrap().writes, 0);
    assert!(fan_rows(&r).is_empty());
    r.stop().await;
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_possible_submission_and_independent_mismatch_pause_without_replay() {
    for fault in 0..5 {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        {
            let mut s = r.port.state.lock().unwrap();
            match fault {
                0 => s.temporary_bad_ack = true,
                1 => s.temporary_replies.extend([0, 0, 1]),
                2 => s.temporary_replies.extend([0, 0, 0]),
                3 => s.fail_write = Some(1),
                _ => {
                    let state = r.port.state.clone();
                    s.battery_getter_hook = Some(Arc::new(move |command, _| {
                        let _ = &state;
                        let _ = command;
                    }));
                    s.temporary_replies.extend([0, 0, 16]);
                }
            }
        }
        let response = r
            .request(battery_request(730, "setFullChargeOverride", json!(true)))
            .await;
        assert_eq!(response["result"]["status"], 2, "{response}");
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        assert!(r.storage.pending());
        let retry = r
            .request(battery_request(731, "setFullChargeOverride", json!(false)))
            .await;
        assert_eq!(retry["result"]["status"], 2, "{retry}");
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        r.stop().await;
    }
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_real_service_loop_battery_event_needs_no_subscriber_and_uses_reserved_ids() {
    let rig = Rig::new(MemoryFiles::default());
    let events: Events = Rc::default();
    let controls = Controls::new();
    let (mut ports, tx) = ports(Fault::None, &events);
    ports.source = rig.source.clone();
    let (running, ready) = oneshot::channel();
    let service = lifecycle(&controls, Ok(ports), &events, move || {
        running.send(()).unwrap();
    });
    let exercise = async {
        ready.await.unwrap();
        let response = outer_request(
            &tx,
            battery_request(740, "setFullChargeOverride", json!(true)),
        )
        .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        let enable = fan_rows(&rig).last().unwrap().clone();
        let ids = enable["tfc"]["cancel_ids"]
            .as_array()
            .unwrap()
            .iter()
            .map(|x| x.as_u64().unwrap() as u8)
            .collect::<Vec<_>>();
        rig.port.exhaust();
        rig.port.state.lock().unwrap().actual = 0;
        rig.metadata.store(1, Ordering::Release);
        crate::scm_host::installed_ingress(
            &rig.custody.lifecycle,
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
            || rig.origin.stop(),
        );
        tokio::time::timeout(HANG_GUARD, async {
            loop {
                if rig.port.state.lock().unwrap().writes == 2 && !rig.storage.pending() {
                    break;
                }
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        assert!(!rig.storage.desired_battery(1).unwrap().temporary);
        let rows = fan_rows(&rig);
        let cancel = rows.last().unwrap();
        assert_eq!(cancel["progress"], 72);
        assert_eq!(cancel["tfc"]["origin"]["request"], enable["request"]);
        assert_eq!(cancel["caller_pid"], enable["caller_pid"]);
        let publication = rig.source.lock().await.publications().unwrap();
        for view in [
            publication.borrow().active.project_private(None),
            publication.borrow().active.project_private(Some(SID)),
            publication.borrow().inactive.clone(),
        ] {
            let bytes = crate::service_status::encode_status_event(&view).unwrap();
            emit_fixture_frame("publisher_projection", "publication", "statusEvent", &bytes);
        }
        let sent = rig.port.state.lock().unwrap().sent.clone();
        assert!(
            sent.iter()
                .filter(|x| ids.contains(&x.3))
                .any(|x| x.1 == 0x0f && x.2 == vec![0])
        );
        crate::scm_host::installed_ingress(
            &rig.custody.lifecycle,
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
            || rig.origin.stop(),
        );
        tokio::time::sleep(Duration::from_millis(550)).await;
        assert_eq!(rig.port.state.lock().unwrap().writes, 2);
        controls.handle(windows_sys::Win32::System::Services::SERVICE_CONTROL_STOP);
    };
    let (result, ()) = tokio::join!(service, exercise);
    result.unwrap();
}

#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_battery_event_lost_logon_or_controller_clears_intent_and_pauses_once() {
    for lose_logon in [true, false] {
        let r = Rig::new(MemoryFiles::default());
        r.get().await;
        let response = r
            .request(battery_request(750, "setFullChargeOverride", json!(true)))
            .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        r.port.state.lock().unwrap().actual = 0;
        r.metadata.store(1, Ordering::Release);
        if lose_logon {
            r.custody.authentication.store(2, Ordering::Release);
        } else {
            r.custody.fresh_refuse.store(true, Ordering::Release);
        }
        r.custody.lifecycle.ingress(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
        );
        r.source.lock().await.process_power_source().await.unwrap();
        assert!(!r.storage.temporary_desired());
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        r.custody.lifecycle.ingress(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
        );
        r.source.lock().await.process_power_source().await.unwrap();
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        let status = r.get().await;
        assert!(
            status["status"]["message"]
                .as_str()
                .unwrap()
                .contains("cancellation is paused"),
            "{status}"
        );
        r.stop().await;
    }
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_original_fan_creator_and_complete_reserved_release_survive_tfc() {
    let r = fan_rig(2, 0);
    fresh_observe(&r).await;
    let response = r.request(fan_request(760, Some(1), 3900)).await;
    assert_eq!(response["result"]["status"], 1, "{response}");
    let owner = fan_rows(&r).last().unwrap().clone();
    let response = r
        .request(battery_request(761, "setFullChargeOverride", json!(true)))
        .await;
    assert_eq!(response["result"]["status"], 1, "{response}");
    let response = r
        .request(battery_request(762, "setFullChargeOverride", json!(false)))
        .await;
    assert_eq!(response["result"]["status"], 1, "{response}");
    r.stop().await;
    let rows = fan_rows(&r);
    let release = rows.last().unwrap();
    assert!(matches!(release["progress"].as_u64(), Some(50 | 51)));
    assert_eq!(release["request"], owner["request"]);
    assert_eq!(release["caller_pid"], owner["caller_pid"]);
    assert_eq!(release["fan"], owner["fan"]);
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_canceled_possible_submission_keeps_global_fence_and_never_source_replays() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    let release = Arc::new(AtomicBool::new(false));
    r.port.state.lock().unwrap().held_write = Some((1, release.clone()));
    let mut request =
        Box::pin(r.request(battery_request(770, "setFullChargeOverride", json!(true))));
    tokio::select! { _=&mut request=>panic!("held setter unexpectedly completed"), _=async {loop {if r.port.state.lock().unwrap().writes==1 {break;}tokio::time::sleep(Duration::from_millis(5)).await;}}=>{} }
    drop(request);
    release.store(true, Ordering::Release);
    r.reader.drain_active().await.unwrap();
    assert!(r.storage.pending());
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    r.port.state.lock().unwrap().actual = 0;
    r.metadata.store(1, Ordering::Release);
    r.custody.lifecycle.ingress(
        windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
        10,
    );
    r.source.lock().await.process_power_source().await.unwrap();
    assert!(!r.storage.temporary_desired());
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    assert!(r.storage.pending());
    r.stop().await;
}

#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_basic_request_before_source_tick_cannot_discard_cancellation_owner() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(780, "setFullChargeOverride", json!(true)))
            .await["result"]["status"],
        1
    );
    r.port.state.lock().unwrap().actual = 0;
    r.metadata.store(1, Ordering::Release);
    r.custody.lifecycle.ingress(
        windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
        10,
    );
    let response = r
        .request(battery_request(781, "setBatteryProtection", json!(false)))
        .await;
    assert_eq!(response["result"]["status"], 2, "{response}");
    assert!(!r.storage.temporary_desired());
    assert_eq!(r.port.state.lock().unwrap().temporary_raw, 4);
    r.source.lock().await.process_power_source().await.unwrap();
    assert_eq!(r.port.state.lock().unwrap().temporary_raw, 0);
    assert_eq!(r.port.state.lock().unwrap().writes, 2);
    assert!(!r.storage.pending());
    r.stop().await;
}
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_unknown_source_recovers_to_battery_in_the_same_event_epoch() {
    let r = Rig::new(MemoryFiles::default());
    r.get().await;
    assert_eq!(
        r.request(battery_request(790, "setFullChargeOverride", json!(true)))
            .await["result"]["status"],
        1
    );
    r.port.state.lock().unwrap().actual = 0;
    r.metadata.store(4, Ordering::Release);
    r.custody.lifecycle.ingress(
        windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
        10,
    );
    r.source.lock().await.process_power_source().await.unwrap();
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    assert!(r.storage.temporary_desired());
    r.metadata.store(1, Ordering::Release);
    r.source.lock().await.process_power_source().await.unwrap();
    assert_eq!(r.port.state.lock().unwrap().writes, 2);
    assert_eq!(r.port.state.lock().unwrap().temporary_raw, 0);
    r.stop().await;
}
/// A rig whose jobs all run on paused time (see `Fixture::worker`): with a
/// latency set, each recipe takes its budget from its request's arrival and
/// spends it only through its own waits.
#[cfg(feature = "service-temporary-charge-v1")]
fn paused_rig(files: MemoryFiles) -> Rig {
    let r = Rig::new(files);
    r.port.state.lock().unwrap().latency = Some(Duration::ZERO);
    r
}
/// Runs `work` with the test clock paused, and never moves it while a job or
/// status-observer thread runs, however slowly the host runs it. On the real
/// clock a loaded host could expire a temporary-charge job after its SET,
/// which leaves the row fenced, as it must after possible submission.
///
/// The Service polls those threads every 5 ms. A thread is counted as exited
/// a few instructions before the Service sees it finished, so each exit buys
/// up to `PER_EXIT` such polls, taken while no counted thread runs. The clock
/// therefore moves at most 5 ms × (`PER_EXIT` × exits) however long the work
/// takes, plus one poll per real second in which nothing else moved it, which
/// only keeps a descheduled thread from hanging the test. The arrival this sets
/// is the budget start for a recipe that serves no request, such as a source
/// cancellation; a request sets its own.
#[cfg(feature = "service-temporary-charge-v1")]
async fn frozen<T>(port: &Fixture, work: impl std::future::Future<Output = T>) -> T {
    tokio::time::pause();
    let now = Some(tokio::time::Instant::now());
    port.arrive(now, None);
    let out = hold_clock(port, work).await;
    tokio::time::resume();
    out
}
/// One rig's claim on the held test clock while one of its exchanges runs.
struct Holder {
    id: u64,
    port: Fixture,
    parks: Rc<dyn Fn() -> bool>,
}
/// The test clock as the rigs of one test hold it. Exchanges of several rigs
/// can overlap (a test joins them), so the clock is paused once, billed from
/// every holder's job threads, and let go when the last holder ends.
struct HeldClocks {
    holders: Vec<Holder>,
    next: u64,
    /// Set while this layer has the clock paused, with the moment it did.
    paused: Option<ClockMark>,
    /// The real instant up to which the held clock has been billed.
    billed: Option<std::time::Instant>,
    /// The test exercises a bound on the real clock, so no exchange holds it.
    real_clock: bool,
}
thread_local! {
    static HELD_CLOCKS: std::cell::RefCell<HeldClocks> = const {
        std::cell::RefCell::new(HeldClocks {
            holders: Vec::new(),
            next: 0,
            paused: None,
            billed: None,
            real_clock: false,
        })
    };
}
/// Leaves the test clock real for the rest of the calling test. A test whose
/// subject is a bound the Service measures on the real clock, with slack the
/// test sets in real time, calls it: its rigs' exchanges would otherwise hold
/// the clock while their job threads work, and rigs that run side by side
/// would hold it back from one another.
#[cfg(feature = "service-profile-triggers-v1")]
fn keep_real_clock() {
    HELD_CLOCKS.with(|held| held.borrow_mut().real_clock = true);
}
impl HeldClocks {
    /// Runs `work` on the held clock, which keeps pace with the real clock
    /// except while a holder's job thread is busy: that time is not billed, so
    /// a loaded host cannot expire a deadline the Service holds on the test
    /// clock while a job thread waits to be scheduled or to finish. Time is
    /// billed as it passes while no job thread runs (a cooldown, say), while
    /// one is parked in a test hook (`Fixture::hooked`), and while the test
    /// parks a job on purpose (`Rig::parks`): those waits end only at the
    /// Service's own bound on the real clock. Unlike `hold_clock`, which also
    /// bounds the polls an idle Service gets, the test is never left waiting on
    /// a timer that no job moves.
    ///
    /// The clock and the Service share the test's thread, so a host that
    /// deschedules that thread stops both, and the time it lost would come
    /// back as one step past the Service's next poll and any deadline behind
    /// it, before the Service has seen a job that finished meanwhile. A step
    /// therefore bills at most `MAX_STEP`, one of the Service's thread polls,
    /// and the rest of such a gap is the host's, not the Service's. A running
    /// thread polls the clock far more often than that, so the clock still
    /// keeps pace with the real one. A wait the test chose is not capped: it
    /// ends at a bound on the real clock, which may race real-time slack the
    /// test set.
    async fn pace<T>(work: impl std::future::Future<Output = T>) -> T {
        const MAX_STEP: Duration = Duration::from_millis(5);
        let finished = std::cell::Cell::new(false);
        let work = async {
            let out = work.await;
            finished.set(true);
            out
        };
        let clock = async {
            while !finished.get() {
                let now = std::time::Instant::now();
                let unbilled = HELD_CLOCKS.with(|held| {
                    let mut held = held.borrow_mut();
                    // A wait the test chose bills every holder's time as the real
                    // clock would: another rig's busy job cannot hold it back.
                    let waiting = held
                        .holders
                        .iter()
                        .any(|holder| holder.port.hooks_running() > 0 || (holder.parks)());
                    let busy = !waiting
                        && held
                            .holders
                            .iter()
                            .any(|holder| holder.port.running_threads() > 0);
                    let billed = held.billed.unwrap_or(now);
                    if busy || now - billed < Duration::from_millis(1) {
                        held.billed = Some(if busy { now } else { billed });
                        return None;
                    }
                    held.billed = Some(now);
                    // A wait the test chose ends at a bound the Service keeps on
                    // the real clock, so it bills all of it.
                    Some(if waiting {
                        now - billed
                    } else {
                        (now - billed).min(MAX_STEP)
                    })
                });
                if let Some(elapsed) = unbilled {
                    tokio::time::advance(elapsed).await;
                }
                tokio::task::yield_now().await;
            }
        };
        let (out, ()) = tokio::join!(work, clock);
        out
    }
}
/// One exchange's hold on the test clock, undone when it ends or is dropped
/// part way, as a test cancels a pending request: arrivals cleared and, for
/// the last holder, the clock running again.
struct Exchange<'a> {
    rig: &'a Rig,
    id: Option<u64>,
}
impl<'a> Exchange<'a> {
    fn enter(rig: &'a Rig) -> Self {
        let parks = rig.parks();
        let id = HELD_CLOCKS.with(|held| {
            // A test that arms a real-time wait before the exchange has its own
            // bound on the real clock to exercise.
            if parks() || held.borrow().real_clock {
                return None;
            }
            let mut held = held.borrow_mut();
            if held.paused.is_none() {
                if clock_is_paused() {
                    return None;
                }
                held.paused = Some(ClockMark::now());
                held.billed = Some(std::time::Instant::now());
                tokio::time::pause();
            }
            let id = held.next;
            held.next += 1;
            held.holders.push(Holder {
                id,
                port: rig.port.clone(),
                parks: parks.clone(),
            });
            Some(id)
        });
        let arrival = Some(tokio::time::Instant::now());
        rig.port.arrive(arrival, arrival);
        Self { rig, id }
    }
    fn holding(&self) -> bool {
        self.id.is_some()
    }
    /// Ends the exchange, bringing the clock level first when it is the last
    /// and `after` asks.
    async fn end(&mut self, after: HeldClock) {
        let last = HELD_CLOCKS.with(|held| {
            let held = held.borrow();
            if held.holders.len() == 1 && self.id.is_some() {
                held.paused
            } else {
                None
            }
        });
        if let (Some(mark), HeldClock::CatchUp) = (last, after) {
            mark.catch_up().await;
        }
        self.release();
    }
    fn release(&mut self) {
        self.rig.port.arrive(None, None);
        let Some(id) = self.id.take() else {
            return;
        };
        HELD_CLOCKS.with(|held| {
            let mut held = held.borrow_mut();
            held.holders.retain(|holder| holder.id != id);
            if held.holders.is_empty() && held.paused.take().is_some() && !std::thread::panicking()
            {
                tokio::time::resume();
            }
        });
    }
}
impl Drop for Exchange<'_> {
    fn drop(&mut self) {
        self.release();
    }
}
/// Whether the test clock is paused. Tokio offers no query, and pausing a
/// paused clock panics. A running clock moves within microseconds; a paused
/// one stays put for as long as it is watched. The last look comes after the
/// whole watch, so a host that deschedules this thread through it has still
/// let real time pass, which a running clock shows.
fn clock_is_paused() -> bool {
    let mark = tokio::time::Instant::now();
    let watched = std::time::Instant::now();
    while watched.elapsed() < Duration::from_millis(2) {
        if tokio::time::Instant::now() != mark {
            return false;
        }
        std::hint::spin_loop();
    }
    tokio::time::Instant::now() == mark
}
/// Runs the test clock again for a rig's Stop (`Rig::drain`), whose exchange
/// then holds it. A test that paused the clock to age a result has it paused
/// still, and the drain's bounds wait on OS threads: the status observer's
/// retirement polls its thread until the drain deadline (`retire_observer`),
/// and a clock paused outside an exchange skips to that deadline within
/// microseconds of real time, before a loaded host need have scheduled the
/// thread at all. A held clock does not move while that thread runs. A clock
/// an exchange holds (`HeldClocks`) is that exchange's to let go.
fn resume_clock_for_stop() {
    let held = HELD_CLOCKS.with(|held| held.borrow().paused.is_some());
    if !held && clock_is_paused() {
        tokio::time::resume();
    }
}
/// Both clocks at one moment, to measure how far a held test clock has fallen
/// behind the real one.
#[derive(Clone, Copy)]
struct ClockMark(std::time::Instant, tokio::time::Instant);
impl ClockMark {
    fn now() -> Self {
        Self(std::time::Instant::now(), tokio::time::Instant::now())
    }
    /// Moves the held test clock forward by the time it has lost since the
    /// mark, while no job is running. A source cancellation measures its 4 s
    /// deadline on a job clock that starts at the real instant; measured under
    /// load, it was refused once the lag reached about 3.7 s.
    async fn catch_up(&self) {
        let held = tokio::time::Instant::now() - self.1;
        tokio::time::advance(self.0.elapsed().saturating_sub(held)).await;
    }
}
/// Runs `case` on a runtime of its own and returns its result.
///
/// A `frozen` section holds the test clock while real time passes, so the
/// clock ends the section behind the real one by the time the section took. A
/// job's own paused clock starts at the real instant, so a deadline taken from
/// the test clock then comes up that much short for a later section on the
/// same runtime. A fresh runtime starts both clocks together.
#[cfg(feature = "service-temporary-charge-v1")]
fn isolated<F: std::future::Future>(case: impl FnOnce() -> F + Send) -> F::Output
where
    F::Output: Send,
{
    std::thread::scope(|scope| {
        scope
            .spawn(|| {
                tokio::runtime::Builder::new_current_thread()
                    .enable_all()
                    .build()
                    .unwrap()
                    .block_on(case())
            })
            .join()
            .unwrap_or_else(|panic| std::panic::resume_unwind(panic))
    })
}
/// Runs `work` on the already paused test clock under the rule `frozen`
/// documents: only `work` itself, and the bounded polls a counted thread
/// exit buys, move it. A test that advances the clock itself uses this
/// directly, so a loaded host cannot turn a status-observer wait into
/// hundreds of milliseconds of virtual time (each 5 ms poll of a slow thread
/// would otherwise be auto-advanced by the idle runtime).
#[cfg(feature = "service-battery-policy-v1")]
async fn hold_clock<T>(port: &Fixture, work: impl std::future::Future<Output = T>) -> T {
    const PER_EXIT: u32 = 4;
    const POLL: Duration = Duration::from_millis(5);
    let finished = std::cell::Cell::new(false);
    let work = async {
        let out = work.await;
        finished.set(true);
        out
    };
    let clock = async {
        let started = std::time::Instant::now();
        let mut moved = started;
        let (mut seen, mut allowance) = (port.exited_threads(), 0);
        while !finished.get() {
            assert!(
                started.elapsed() < Duration::from_secs(120),
                "frozen work waited on a timer no counted thread advances"
            );
            let exited = port.exited_threads();
            if exited != seen {
                (seen, allowance) = (exited, PER_EXIT);
            }
            let idle = port.running_threads() == 0;
            if idle && (allowance > 0 || moved.elapsed() > Duration::from_secs(1)) {
                allowance = allowance.saturating_sub(1);
                moved = std::time::Instant::now();
                tokio::time::advance(POLL).await;
                // Let the Service observe this poll before the next one.
                for _ in 0..64 {
                    tokio::task::yield_now().await;
                }
            } else {
                tokio::task::yield_now().await;
            }
        }
    };
    let (out, ()) = tokio::join!(work, clock);
    out
}
/// The IDs and cancellation IDs of the last temporary-charge row for `enabled`.
#[cfg(feature = "service-temporary-charge-v1")]
fn temporary_charge_ids(files: &MemoryFiles, enabled: bool) -> (Value, Value) {
    let journal = files.read("thermal-mutation.journal").unwrap().unwrap();
    let row = journal
        .split(|b| *b == b'\n')
        .filter(|row| !row.is_empty())
        .map(|row| serde_json::from_slice::<Value>(row).unwrap())
        .rfind(|row| row["tfc"]["requested"]["enabled"] == enabled)
        .unwrap();
    (row["tfc"]["ids"].clone(), row["tfc"]["cancel_ids"].clone())
}
// The journal segment, not the transaction pool, is near capacity. Its rows
// carry decimal transaction IDs, so the sizes measured on the probes hold only
// while the final rig issues the same IDs; the test checks that rather than
// assume it.
#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_near_capacity_performance_refuses_before_spending_reserved_source_cancel() {
    // Each probe rig runs on a runtime of its own (see `isolated`).
    let (enable, cancellation, probed) = isolated(|| async {
        let probe = paused_rig(MemoryFiles::default());
        probe.get().await;
        let enabled = probe.request(battery_request(800, "setFullChargeOverride", json!(true)));
        assert_eq!(frozen(&probe.port, enabled).await["result"]["status"], 1);
        let enable = probe
            .files
            .read("thermal-mutation.journal")
            .unwrap()
            .unwrap()
            .len();
        let cancellation = probe.files.last_reservation.load(Ordering::Acquire) - (enable + 1);
        let probed = temporary_charge_ids(&probe.files, true);
        probe.stop().await;
        (enable, cancellation, probed)
    });
    let performance_budget = isolated(|| async {
        let performance = paused_rig(MemoryFiles::default());
        performance.get().await;
        frozen(&performance.port, performance.set(1, value(1, 1))).await;
        let performance_budget = performance.files.last_reservation.load(Ordering::Acquire);
        assert!(performance_budget > 0);
        performance.stop().await;
        performance_budget
    });
    let target = crate::thermal_history::SEGMENT - (enable + cancellation + performance_budget - 1);
    let mut journal = Vec::new();
    let mut number = 100000;
    while journal.len() + 900 < target {
        for progress in [0, 6] {
            let row = json!({"request":format!("aaaaaaaa-bbbb-cccc-dddd-{number:012}"),"owner":"7".repeat(64),"user_sid":SID,"caller_pid":44,"session":7,"requested":null,"destination":1,"intent":format!("{:x}",<sha2::Sha256 as sha2::Digest>::digest(b"null")),"progress":progress});
            journal.extend(serde_json::to_vec(&row).unwrap());
            journal.push(b'\n');
        }
        number += 1;
    }
    let padding = target - journal.len();
    journal.splice(
        journal.len() - 1..journal.len() - 1,
        std::iter::repeat_n(b' ', padding),
    );
    assert_eq!(journal.len(), target);
    let files = MemoryFiles::default();
    files
        .bytes
        .lock()
        .unwrap()
        .insert("thermal-mutation.journal".into(), journal);
    let r = paused_rig(files);
    r.get().await;
    // One held section: each held section leaves the test clock behind the
    // real one by the real time it took, which a later job's deadline feels.
    frozen(&r.port, async {
        let mark = ClockMark::now();
        let response = r
            .request(battery_request(801, "setFullChargeOverride", json!(true)))
            .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        let enabled = temporary_charge_ids(&r.files, true);
        assert_eq!(enabled, probed);
        let response = r.set(2, value(1, 1)).await;
        assert_ne!(response["result"]["status"], 1, "{response}");
        // A repeated refusal answers the same commands again, which frees the
        // first one's IDs, so it keeps no further IDs out of the pool.
        let free = r.port.free_transactions();
        let response = r.set(3, value(1, 1)).await;
        assert_ne!(response["result"]["status"], 1, "{response}");
        assert_eq!(r.port.free_transactions(), free);
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        r.port.state.lock().unwrap().actual = 0;
        r.metadata.store(1, Ordering::Release);
        r.custody.lifecycle.ingress(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
        );
        mark.catch_up().await;
        // The cancellation serves no request, so it takes its budget from here.
        r.port.arrive(Some(tokio::time::Instant::now()), None);
        r.source.lock().await.process_power_source().await.unwrap();
        assert_eq!(r.port.state.lock().unwrap().writes, 2);
        assert_eq!(r.port.state.lock().unwrap().temporary_raw, 0);
        // The cancellation spent exactly the IDs the enable reserved for it.
        assert_eq!(temporary_charge_ids(&r.files, false).0, enabled.1);
        assert!(!r.storage.pending());
    })
    .await;
    r.stop().await;
}

#[cfg(feature = "service-temporary-charge-v1")]
#[tokio::test]
async fn tfc_v1_unrepresentable_private_intent_keeps_evidence_and_both_actual_paths_compatible() {
    let files = MemoryFiles::default();
    files.profiles(1);
    let r = Rig::new(files);
    r.get().await;
    assert_eq!(
        r.request(battery_request(820, "setFullChargeOverride", json!(true)))
            .await["result"]["status"],
        1
    );
    let mut profiles = r.files.document("profiles.json");
    profiles["profiles"]["pluggedIn"]["batteryProtectionEnabled"] = json!(false);
    r.files.put("profiles.json", profiles);
    let profile =
        crate::configuration::digest_hex(&r.files.read("profiles.json").unwrap().unwrap());
    let preferences = crate::configuration::digest_hex(
        &r.files.read("battery-preferences.json").unwrap().unwrap(),
    );
    r.files.put(
        "battery-configuration.commit.json",
        json!({"schemaVersion":crate::configuration::CURRENT_SCHEMA,"profileSha256":profile,"preferencesSha256":preferences}),
    );
    let refresh = r.get().await;
    assert!(
        refresh["status"]["message"]
            .as_str()
            .unwrap()
            .contains("Saved private policy is unavailable"),
        "{refresh}"
    );
    assert!(refresh["status"]["confirmedState"]["battery"]["temporaryFullCharge"] == true);
    assert!(r.storage.desired_battery(0).unwrap().temporary);
    assert!(!r.storage.desired_battery(0).unwrap().protection);
    let refused = r
        .request(battery_request(821, "setBatteryProtection", json!(false)))
        .await;
    assert_eq!(refused["result"]["status"], 2, "{refused}");
    let refresh = r.get().await;
    assert!(
        refresh["status"]["message"]
            .as_str()
            .unwrap()
            .contains("Saved private policy is unavailable"),
        "{refresh}"
    );
    assert!(r.storage.temporary_desired());
    assert_eq!(r.port.state.lock().unwrap().writes, 1);
    let cancel = r
        .request(battery_request(822, "setFullChargeOverride", json!(false)))
        .await;
    assert_eq!(cancel["result"]["status"], 1, "{cancel}");
    assert_eq!(r.port.state.lock().unwrap().writes, 2);
    assert!(!r.storage.temporary_desired());
    assert_eq!(
        r.files.document("profiles.json")["profiles"]["pluggedIn"]["batteryProtectionEnabled"],
        false
    );
    r.stop().await;
}

#[cfg(feature = "service-profiles-v1")]
#[path = "profiles.rs"]
mod profiles;

#[cfg(feature = "service-profile-triggers-v1")]
#[path = "request_triggers.rs"]
mod request_triggers;

#[cfg(feature = "service-profile-triggers-v1")]
#[path = "thermal_policy.rs"]
mod thermal_policy;

#[cfg(feature = "service-brightness-hid-usage-v1")]
#[path = "display_brightness.rs"]
mod display_brightness;
#[cfg(feature = "service-fan-stop-v1")]
#[path = "fan_stop.rs"]
mod fan_stop;

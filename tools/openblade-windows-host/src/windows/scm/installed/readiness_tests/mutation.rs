//! Prelisted fake-I/O installed dispatcher tests. Every root, SID, authority,
//! metadata sample and native port is injected before a default resolver exists.
use super::*;
use crate::discovery::performance::{ReadAdmission, mutation_fixture::Fixture};
use crate::installed::readiness::{
    mailbox_read::{MailboxSource, MutationFactories, ReadCustody, ReadSource},
    status_source::{StatusOrigin, injected_mutation_source},
    thermal_mutation::{Fresh, Runtime},
    thermal_storage::{Files, Storage},
};
use std::{
    collections::BTreeMap,
    sync::{
        Arc, Mutex as StdMutex,
        atomic::{AtomicBool, AtomicU8, AtomicUsize, Ordering},
    },
};
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
    reserve_uncertain: Arc<AtomicBool>,
    fail_terminal: Arc<AtomicBool>,
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
        let fixture:Value=serde_json::from_str(include_str!("../../../../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-aggregate.json")).unwrap();
        let profile: Value = serde_json::from_str(fixture["prior"][0].as_str().unwrap()).unwrap();
        let mut profiles = profile["profiles"].clone();
        profiles["pluggedIn"]["performance"] = json!(performance);
        profiles["pluggedIn"]["gpuClockTuning"] = Value::Null;
        profiles["applicationProfiles"] = json!([{ "id":"11111111-2222-3333-4444-555555555555","name":"Game","executablePath":"C:\\Games\\Game.exe","activationMode":0,"enabled":true,"overrides":{"pluggedIn":{"performance":2}}}]);
        self.put(
            "profiles.json",
            json!({"schemaVersion":25,"profiles":profiles}),
        );
        self.put(
            "battery-preferences.json",
            serde_json::from_str(fixture["prior"][1].as_str().unwrap()).unwrap(),
        );
        let bytes = self.bytes.lock().unwrap();
        let profile = crate::configuration::digest_hex(&bytes["profiles.json"]);
        let prefs = crate::configuration::digest_hex(&bytes["battery-preferences.json"]);
        drop(bytes);
        self.put(
            "battery-configuration.commit.json",
            json!({"schemaVersion":25,"profileSha256":profile,"preferencesSha256":prefs}),
        );
    }
}
impl Files for MemoryFiles {
    fn reserve(&self, expected: Option<&[u8]>, needed: usize) -> io::Result<Option<Vec<u8>>> {
        if self.reserve_uncertain.load(Ordering::Acquire) {
            return Err(io::Error::other("injected uncertain reservation I/O"));
        }
        if self.capacity_refuse.load(Ordering::Acquire) {
            return Err(crate::thermal_history::capacity_refusal());
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
        Ok(self.bytes.lock().unwrap().get(name).cloned())
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if name == "thermal-mutation.journal" {
            let progress = bytes
                .split(|b| *b == b'\n')
                .filter(|line| !line.is_empty())
                .last()
                .map(|line| {
                    serde_json::from_slice::<Value>(line).unwrap()["progress"]
                        .as_u64()
                        .unwrap()
                });
            if progress == Some(1) {
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
                    assert!(files.contains_key("manual-overrides.json"));
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
                .filter(|line| !line.is_empty())
                .last()
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
                .filter(|line| !line.is_empty())
                .last()
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
struct Custody {
    live: AtomicBool,
    origin_for_fan: StdMutex<Option<Arc<crate::origin_lifetime::Lifetime>>>,
    authority_for_fan: AtomicU8,
    fresh_refuse: AtomicBool,
    fatal_health: AtomicBool,
    integrity_lost: AtomicBool,
    activation_refusal: AtomicU8,
    lifecycle: crate::scm_host::Lifecycle,
    activations: AtomicUsize,
    retired: AtomicBool,
    port: Arc<StdMutex<crate::discovery::performance::mutation_fixture::State>>,
}
impl Custody {
    fn new(port: &Fixture) -> Arc<Self> {
        Arc::new(Self {
            port: port.state.clone(),
            live: AtomicBool::new(true),
            origin_for_fan: StdMutex::new(None),
            authority_for_fan: AtomicU8::new(0),
            fresh_refuse: AtomicBool::new(false),
            fatal_health: AtomicBool::new(false),
            integrity_lost: AtomicBool::new(false),
            activation_refusal: AtomicU8::new(0),
            lifecycle: Default::default(),
            activations: AtomicUsize::new(0),
            retired: AtomicBool::new(false),
        })
    }
}
impl ReadCustody for Custody {
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
            generation: [7; 32],
        })
    }
    fn verify_integrity(&self) -> io::Result<()> {
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
    fn authorize(&self) -> Option<ReadAdmission> {
        if let Some(origin) = &*self.origin_for_fan.lock().unwrap() {
            let proof = ReadAdmission {
                lifecycle: self.lifecycle.active()?,
                generation: [7; 32],
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
            generation: [7; 32],
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
            1 => {
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
    fn authorize_owned_stop(&self) -> Option<ReadAdmission> {
        self.verify_health().ok()?;
        if self.fresh_refuse.load(Ordering::Acquire) {
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
        if self.fresh_refuse.load(Ordering::Acquire) || self.fatal_health.load(Ordering::Acquire) {
            return None;
        }
        ReadCustody::authorize(self)
    }
}
struct Origin(Arc<crate::loaded_origin::lifetime::Lifetime>);
impl StatusOrigin for Origin {
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
struct Rig {
    source: Rc<Mutex<Box<dyn StatusSource>>>,
    port: Fixture,
    files: MemoryFiles,
    custody: Arc<Custody>,
    metadata: Arc<AtomicU8>,
    entered: Rc<std::cell::Cell<u64>>,
    reader: ReadSource,
    origin: Arc<crate::loaded_origin::lifetime::Lifetime>,
    fatal_capture: Arc<AtomicBool>,
}
impl Rig {
    fn new(files: MemoryFiles) -> Self {
        let port = Fixture::new();
        let custody = Custody::new(&port);
        let persisted = files.clone();
        port.state.lock().unwrap().before_write = Some(Arc::new(move |number| {
            assert!(persisted.read("manual-overrides.json").unwrap().is_some());
            let journal = persisted.read("thermal-mutation.journal").unwrap().unwrap();
            let last = journal
                .split(|b| *b == b'\n')
                .filter(|s| !s.is_empty())
                .last()
                .unwrap();
            let entry: Value = serde_json::from_slice(last).unwrap();
            if entry.get("fan").is_some() {
                assert!(matches!(
                    entry["progress"].as_u64(),
                    Some(10 | 12 | 14 | 16 | 40 | 42)
                ));
            } else {
                assert_eq!(entry["progress"], if number % 2 == 1 { 1 } else { 3 });
            }
            assert_eq!(entry["user_sid"], SID);
        }));
        let read = port.clone();
        let write = port.clone();
        let reader = ReadSource::injected_mutation(
            custody.clone(),
            MutationFactories {
                read: Box::new(move |custody| read.read(Arc::new(move || custody.authorize()))),
                write: Box::new(move |permit| write.write(permit)),
            },
        );
        let storage = Storage::open(Box::new(files.clone()), SID).unwrap();
        let fresh = custody.clone();
        let fatal_capture = Arc::new(AtomicBool::new(false));
        let capture = fatal_capture.clone();
        let mailbox = MailboxSource::Reader(reader.clone());
        let runtime = Runtime::injected(&mailbox, move |sid, session, _| {
            if capture.load(Ordering::Acquire) {
                return Err(
                    crate::installed::readiness::thermal_mutation::fatal_admission(
                        "injected lifecycle capture lock poisoned",
                    ),
                );
            }
            assert_eq!(sid, SID);
            assert_eq!(session, 7);
            Ok((storage.clone(), fresh.clone()))
        });
        let entered = runtime.entered.clone();
        let metadata = Arc::new(AtomicU8::new(0));
        let origin = Arc::new(crate::loaded_origin::lifetime::Lifetime::default());
        let source = injected_mutation_source(
            Arc::new(Origin(origin.clone())),
            mailbox,
            runtime,
            metadata.clone(),
        );
        Self {
            source: Rc::new(Mutex::new(source)),
            port,
            files,
            custody,
            metadata,
            entered,
            reader,
            origin,
            fatal_capture,
        }
    }
    async fn request(&self, request: Value) -> Value {
        self.request_as(request, USER, SID).await
    }
    async fn request_as(&self, request: Value, caller: Caller, sid: &str) -> Value {
        let operation = request["operation"].as_str().unwrap().to_owned();
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
            crate::service_frame::write_payload(
                &mut client,
                &serde_json::to_vec(&request).unwrap(),
            )
            .await
            .unwrap();
            let bytes = crate::service_frame::read_payload(&mut client)
                .await
                .unwrap();
            (serde_json::from_slice::<Value>(&bytes).unwrap(), bytes)
        };
        let (server, (response, bytes)) = tokio::join!(server, client);
        assert!(server.is_ok());
        // Preserve the actual encoder bytes. Re-serializing Value sorts object
        // keys and can move the managed polymorphic discriminator out of first place.
        assert!(bytes.starts_with(br#"{"frame":"response","#));
        println!(
            "NATIVE_THERMAL_MUTATION_RESPONSE {}",
            std::str::from_utf8(&bytes).unwrap()
        );
        println!(
            "NATIVE_THERMAL_EDGE_TRACE {} {}",
            operation,
            std::str::from_utf8(&bytes).unwrap()
        );
        response
    }
    async fn get(&self) -> Value {
        self.request(json!({"operation":"getStatus","version":80,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000099"})).await
    }
    async fn set(&self, n: u8, value: Value) -> Value {
        let response = self.request(json!({"operation":"setPerformanceOverride","version":80,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"value":value})).await;
        assert!(
            response["status"].is_null(),
            "v80 result replies have exactly one body"
        );
        assert!(!response["result"].is_null());
        response
    }
    async fn stop(&self) {
        let mut source = self.source.lock().await;
        source.drain().await.unwrap();
        source.confirm_drained().unwrap();
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
    let initial=rig.request(json!({"operation":"observeState","version":80,"requestId":"dddddddd-bbbb-cccc-dddd-000000000001","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})).await;
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
    drop(sent);
    let mut source = rig.source.lock().await;
    let projected = source.snapshot(USER, Instant::now()).await.unwrap();
    let projected = serde_json::to_value(projected).unwrap();
    assert_eq!(projected["performanceOverride"]["value"], 1);
    assert_eq!(projected["desiredState"]["performance"]["value"], 1);
    assert!(
        projected["message"]
            .as_str()
            .unwrap()
            .contains("Saved performance policy")
    );
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
    let report=rig.request(json!({"operation":"reportApplicationContext","version":80,"requestId":"bbbbbbbb-bbbb-cccc-dddd-000000000003","instanceId":"cccccccc-bbbb-cccc-dddd-000000000003","sequence":1,"executablePath":"C:\\Games\\Game.exe"})).await;
    assert_eq!(report["result"]["status"], 0);
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
    assert_eq!(result["result"]["status"], 0);
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    assert!(
        result["result"]["message"]
            .as_str()
            .unwrap()
            .contains("GPU")
    );
    assert_eq!(
        rig.files.document("manual-overrides.json")["overrides"]["gpuClockTuning"]["offsets"]["coreMegahertz"],
        10
    );
    rig.stop().await;
}

#[tokio::test]
async fn installed_source_ineligible_intent_is_stored_and_power_change_stops_partial_pair() {
    for actual in [0, 1] {
        let files = MemoryFiles::default();
        files.profiles(1);
        let rig = Rig::new(files);
        rig.port.state.lock().unwrap().actual = actual;
        let result = rig.set(1, value(2, 1)).await;
        let status = rig.get().await;
        assert_eq!(status["status"]["writesBlocked"], true);
        assert_eq!(result["result"]["status"], 0);
        assert_eq!(status["status"]["performanceOverride"]["value"], 2);
        if actual == 0 {
            assert_eq!(rig.port.state.lock().unwrap().writes, 0)
        } else {
            assert_eq!(status["status"]["desiredState"]["performance"]["value"], 1);
            assert_eq!(status["status"]["confirmedState"]["performance"], 1);
        }
        rig.stop().await;
    }
    let rig = Rig::new(MemoryFiles::default());
    rig.port.state.lock().unwrap().change_power = Some((3, 1));
    let result = rig.set(2, value(2, 1)).await;
    let status = rig.get().await;
    assert_eq!(status["status"]["writesBlocked"], true);
    assert_eq!(result["result"]["status"], 2);
    assert_eq!(rig.port.state.lock().unwrap().writes, 1);
    assert_eq!(
        Storage::open(Box::new(rig.files.clone()), SID)
            .unwrap()
            .progress(),
        Some(2)
    );
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
        tokio::select! {_ = &mut request=>panic!("held mutation callback completed"), result = tokio::time::timeout(std::time::Duration::from_secs(2),entered)=>{result.unwrap();}}
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
    drop(state);
    rig.stop().await;
}
#[test]
fn installed_storage_preserves_future_legacy_corrupt_and_uncommitted_documents() {
    for bytes in [
        br#"{"schemaVersion":9,"overrides":{}}"#.as_slice(),
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
        json!({"schemaVersion":25,"profileSha256":"wrong","preferencesSha256":"wrong"}),
    );
    assert!(Storage::open(Box::new(files.clone()), SID).is_err());
    assert_eq!(files.read("profiles.json").unwrap().unwrap(), before);
    assert!(files.read("manual-overrides.json").unwrap().is_none());
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
    assert_eq!(storage.desired(0, None).unwrap().unwrap().0 as i32, 2);
    storage.save(Some(override_value(1, 1)), 0).unwrap();
    assert_eq!(storage.desired(0, None).unwrap().unwrap().1, 4);
    storage.save(None, 0).unwrap();
    assert_eq!(storage.desired(0, None).unwrap().unwrap().1, 1);
    storage.save(Some(override_value(2, 0)), 0).unwrap();
    assert_eq!(storage.desired(2, None).unwrap().unwrap().0 as i32, 1);
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
        let request = json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000077","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0});
        // Get/subscription DTOs reject observation-only members.
        let request = if operation == "observeState" {
            request
        } else {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000077"})
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
        let port = rig.port.state.lock().unwrap();
        assert_eq!(port.writes, writes);
        assert_eq!(
            port.opened, port.retired,
            "actual native callback retirement"
        );
        drop(port);
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
async fn fresh_observe(rig: &Rig) -> Value {
    assert_eq!(rig.get().await["success"], true); // Same metadata readiness probe as installed prepare.
    rig.request(json!({"operation":"observeState","version":80,"requestId":"eeeeeeee-bbbb-cccc-dddd-000000000001","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})).await
}
#[tokio::test]
async fn correction_manual_active0581_stores_without_setters_and_automatic_stays_direct() {
    for selector in [4, 2, 0] {
        let rig = Rig::new(manual_files());
        rig.port.state.lock().unwrap().selectors = [selector, selector];
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
        let state = rig.port.state.lock().unwrap();
        assert_eq!(state.writes, 0);
        assert_eq!(state.opened, state.retired);
        assert_eq!(state.opened > before, refusal == 1);
        if refusal == 1 {
            assert_eq!(observed["success"], true);
        }
        drop(state);
        assert_eq!(rig.custody.activations.load(Ordering::Acquire), 0);
        rig.stop().await;
    }
}
#[tokio::test]
async fn correction_prepared_only_settles_after_drain_and_allows_fresh_request() {
    let rig = Rig::new(MemoryFiles::default());
    rig.port.state.lock().unwrap().change_power = Some((2, 0));
    let response = rig.set(91, value(1, 1)).await;
    assert_eq!(response["result"]["status"], 0);
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
    rig.port.reserve_for_tests(122);
    let response = rig.set(91, value(1, 1)).await;
    assert_eq!(response["result"]["status"], 0);
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
    let held = Arc::new(AtomicBool::new(false));
    let release = Arc::new(AtomicBool::new(false));
    let entered = held.clone();
    let gate = release.clone();
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        entered.store(true, Ordering::Release);
        while !gate.load(Ordering::Acquire) {
            std::thread::yield_now();
        }
    }));
    let request=crate::service_status::StatusRequest::decode(&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000091","value":value(1,1)})).unwrap()).unwrap();
    let deadline = Instant::now() + std::time::Duration::from_secs(1);
    let operation = rig
        .source
        .lock()
        .await
        .mutate(USER, SID.into(), request, deadline);
    tokio::pin!(operation);
    let boundary = async {
        while !held.load(Ordering::Acquire) {
            tokio::task::yield_now().await;
        }
    };
    tokio::select! {_=&mut operation=>panic!("retirement unexpectedly completed"),result = tokio::time::timeout(std::time::Duration::from_secs(2),boundary)=>result.unwrap()}
    tokio::time::sleep_until(deadline).await;
    release.store(true, Ordering::Release);
    let (result, status) = operation.await.unwrap();
    assert_eq!(result.status, 2);
    assert!(status.is_none());
    let count = rig.port.state.lock().unwrap().sent.len();
    let observed = fresh_observe(&rig).await;
    assert_eq!(observed["status"]["performanceOverride"]["value"], 1);
    assert_eq!(observed["status"]["confirmedState"]["performance"], 1);
    let state = rig.port.state.lock().unwrap();
    assert!(state.sent.len() > count);
    assert_eq!(state.writes, 2);
    drop(state);
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
        crate::service_frame::write_payload(&mut client,&serde_json::to_vec(&json!({"operation":"subscribeStatus","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000090"})).unwrap()).await.unwrap();
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
        tokio::select! {_=&mut a=>panic!("held A completed"),v=tokio::time::timeout(std::time::Duration::from_secs(2),entered)=>v.unwrap()}
        let mut desired = value(2, 1);
        desired["gpuClockTuning"] =
            json!({"enabled":true,"offsets":{"coreMegahertz":10,"memoryMegahertz":20}});
        let request=crate::service_status::StatusRequest::decode(&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000092","value":desired})).unwrap()).unwrap();
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
        assert_eq!(result.status, 0);
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
        let mut request = json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000093"});
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
    serde_json::from_slice(
        &crate::service_frame::read_payload(&mut client)
            .await
            .unwrap(),
    )
    .unwrap()
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
        let get = || json!({"operation":"getStatus","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000094"});
        let observe = || json!({"operation":"observeState","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000095","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0});
        assert_eq!(outer_request(&tx, observe()).await["success"], true);
        rig.custody.fresh_refuse.store(true, Ordering::Release);
        let refused=outer_request(&tx,json!({"operation":"setPerformanceOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096","value":value(1,1)})).await;
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
    tokio::select! {_=&mut request=>panic!("unretired operation completed"),r=tokio::time::timeout(std::time::Duration::from_secs(2),reached)=>r.unwrap()}
    let prepared = rig.files.read("thermal-mutation.journal").unwrap().unwrap();
    let prior = Storage::open(Box::new(rig.files.clone()), SID).unwrap();
    assert_eq!(prior.progress(), Some(0));
    assert!(prior.pending());
    assert_eq!(rig.port.state.lock().unwrap().writes, 0);
    released.store(true, Ordering::Release);
    assert_eq!(request.await["result"]["status"], 0);
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
            crate::service_frame::write_payload(&mut client,&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})).unwrap()).await.unwrap();
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
    assert_eq!(rig.set(91, desired).await["result"]["status"], 0);
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
    let observed = rig
        .source
        .lock()
        .await
        .observe_performance(USER, Instant::now() + std::time::Duration::from_secs(2))
        .await
        .unwrap()
        .unwrap();
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
    assert!(message.contains("saved, not applied") && message.contains("new performance request"));
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
            tokio::select! { _ = &mut observation => panic!("held getter completed"), r = tokio::time::timeout(std::time::Duration::from_secs(2), opened) => r.unwrap() }
        } // Cancel the actual read future, leaving its native callback held.
        {
            let request = rig.set(91, value(1, 1));
            tokio::pin!(request);
            let entered = async {
                while rig.entered.get() == 0 {
                    tokio::task::yield_now().await;
                }
            };
            tokio::select! { _ = &mut request => panic!("request escaped held pre-activation drain"), r = tokio::time::timeout(std::time::Duration::from_secs(2), entered) => r.unwrap() }
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
        let state = rig.port.state.lock().unwrap();
        assert_eq!(state.opened, state.retired);
        assert!(state.opened > 1);
        assert_eq!(state.writes, 0);
        drop(state);
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
        assert_eq!(rig.set(91, saved).await["result"]["status"], 0);
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
                json!({"operation":"observeState","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
            } else {
                json!({"operation":"getStatus","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098"})
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
        let (result, ()) = tokio::join!(service, exercise);
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
    let request = crate::service_status::StatusRequest::decode(&serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})).unwrap()).unwrap();
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
        crate::service_frame::write_payload(&mut client, &serde_json::to_vec(&json!({"operation":"subscribeStatus","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000090"})).unwrap()).await.unwrap();
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
        tokio::select! { _ = &mut request => panic!("retirement must remain held"), result = tokio::time::timeout(std::time::Duration::from_secs(2), held) => result.unwrap() }
        assert_eq!(
            rig.files.document("manual-overrides.json")["overrides"]["performance"],
            Value::Null
        );
        rig.metadata.store(1, Ordering::Release);
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
        assert_eq!(request.await["result"]["status"], 0);
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
        println!(
            "NATIVE_THERMAL_EDGE_AFTER_STORED {}",
            std::str::from_utf8(&bytes).unwrap()
        );
        drop(client);
    };
    let (result, ()) = tokio::join!(server, exercise);
    assert!(result.is_ok());
    let foreign = rig.request_as(json!({"operation":"getStatus","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000099"}), USER, "S-1-5-21-111-222-333-1002").await;
    assert!(foreign["status"]["performanceOverride"].is_null());
    rig.stop().await;
}

fn managed_application_files(bytes: &[u8]) -> MemoryFiles {
    let files = MemoryFiles::default();
    files.profiles(1);
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
        json!({"schemaVersion":25,"profileSha256":profile,"preferencesSha256":preferences}),
    );
    files
}
async fn report_game(rig: &Rig) {
    let result = rig.request(json!({"operation":"reportApplicationContext","version":80,"requestId":"bbbbbbbb-bbbb-cccc-dddd-000000000003","instanceId":"cccccccc-bbbb-cccc-dddd-000000000003","sequence":1,"executablePath":r"C:\Games\Game.exe"})).await;
    assert_eq!(result["result"]["status"], 0);
}
#[tokio::test]
async fn edge_managed_fan_control_only_application_precedence_and_lease_are_composed() {
    let fixed = include_bytes!("../../../../../tests/fixtures/app-fans-0-1.json").as_slice();
    let curve = include_bytes!("../../../../../tests/fixtures/app-fans-0-2.json").as_slice();
    let automatic = include_bytes!("../../../../../tests/fixtures/app-fans-1-0.json").as_slice();
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
    let port = rig.port.state.lock().unwrap();
    assert_eq!(port.writes, 2);
    assert_eq!(
        port.sent
            .iter()
            .filter(|(class, command, _, _)| *class == 13 && *command == 2)
            .count(),
        2
    );
    drop(port);
    rig.stop().await;
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
        serde_json::from_value(custom_request).unwrap();
    assert!(
        custom.validate().is_err(),
        "saved Custom never grants a Custom setter"
    );
    for enabled in [false, true] {
        let rig = Rig::new(MemoryFiles::default());
        let mut request = value(1, 1);
        request["gpuClockTuning"] = gpu.clone();
        request["gpuClockTuning"]["enabled"] = json!(enabled);
        if !enabled {
            request["gpuClockTuning"]["effectiveOffsets"] =
                json!({"coreMegahertz":0,"memoryMegahertz":0});
        }
        assert_eq!(rig.set(91, request).await["result"]["status"], 0);
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
            let mut request = json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000078"});
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
            crate::service_frame::write_payload(&mut client, &serde_json::to_vec(&json!({"operation":"subscribeStatus","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000090"})).unwrap()).await.unwrap();
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
            println!(
                "NATIVE_THERMAL_EDGE_FRAME {}",
                std::str::from_utf8(&raw).unwrap()
            );
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
        tokio::select! { _ = &mut request => panic!("request completed before actual retirement release"), r = tokio::time::timeout(std::time::Duration::from_secs(2), held) => r.unwrap() }
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
            &serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":80,
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
                &serde_json::to_vec(&json!({"operation":"setPerformanceOverride","version":80,
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
    let reached = Arc::new(AtomicBool::new(false));
    let release = Arc::new(AtomicBool::new(false));
    let entered = reached.clone();
    let gate = release.clone();
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        entered.store(true, Ordering::Release);
        while !gate.load(Ordering::Acquire) {
            std::thread::yield_now();
        }
    }));
    {
        let request = rig.set(91, value(1, 1));
        tokio::pin!(request);
        let held = async {
            while !reached.load(Ordering::Acquire) {
                tokio::task::yield_now().await;
            }
        };
        tokio::select! { _ = &mut request => panic!("fatal request completed before controlled retirement release"), r = tokio::time::timeout(std::time::Duration::from_secs(2), held) => r.unwrap() }
    }
    Fresh::verify_health(rig.custody.as_ref()).unwrap();
    assert_eq!(rig.files.first_intent_attempts.load(Ordering::Acquire), 1);
    let prepared = rig.files.read("thermal-mutation.journal").unwrap();
    release.store(true, Ordering::Release);
    for (operation, sid) in [
        ("getStatus", SID),
        ("observeState", SID),
        ("setPerformanceOverride", "S-1-5-21-111-222-333-1002"),
    ] {
        let request = if operation == "setPerformanceOverride" {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})
        } else if operation == "observeState" {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000097","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
        } else {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096"})
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
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})
        } else if operation == "observeState" {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000097","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
        } else {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096"})
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
                if progress == 5 { 1 } else { 0 }
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
        tokio::select! { _ = &mut request => panic!("retirement must remain held"), result = tokio::time::timeout(std::time::Duration::from_secs(2), held) => result.unwrap() }
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
            let witness = rig
                .source
                .lock()
                .await
                .observe_performance(USER, Instant::now() + std::time::Duration::from_secs(2))
                .await
                .unwrap()
                .unwrap();
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
        report_game(&rig).await;
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
    let reached = Arc::new(AtomicBool::new(false));
    let release = Arc::new(AtomicBool::new(false));
    struct Release(Arc<AtomicBool>);
    impl Drop for Release {
        fn drop(&mut self) {
            self.0.store(true, Ordering::Release);
        }
    }
    let _release_on_failure = Release(release.clone());
    let entered = reached.clone();
    let gate = release.clone();
    rig.port.state.lock().unwrap().on_mutation_retired = Some(Arc::new(move || {
        entered.store(true, Ordering::Release);
        while !gate.load(Ordering::Acquire) {
            std::thread::yield_now();
        }
    }));
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
                "operation":"setPerformanceOverride","version":80,
                "requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000091","value":value(1,1)
            }))
            .unwrap(),
        )
        .await
        .unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(2), async {
            while !reached.load(Ordering::Acquire) {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
        // Exercise serve_mutation's real biased disconnect branch while the
        // accepted mutation future is still waiting for actual retirement.
        drop(client);
    };
    let (server, ()) = tokio::time::timeout(std::time::Duration::from_secs(3), async {
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
    release.store(true, Ordering::Release);
    for (operation, sid) in [
        ("getStatus", SID),
        ("observeState", SID),
        ("setPerformanceOverride", "S-1-5-21-111-222-333-1002"),
    ] {
        let request = if operation == "setPerformanceOverride" {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000098","value":value(1,1)})
        } else if operation == "observeState" {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000097","includeLiveReadings":false,"requestedLiveReadings":0,"effectiveLiveReadings":0})
        } else {
            json!({"operation":operation,"version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000096"})
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
                "operation":"setPerformanceOverride", "version":80,
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
    let files = MemoryFiles::default();
    files.profiles(1);
    files.capacity_refuse.store(true, Ordering::Release);
    let rig = Rig::new(files);
    let result = rig.set(99, value(2, 1)).await;
    assert_eq!(result["result"]["status"], 2);
    assert!(
        result["result"]["message"]
            .as_str()
            .unwrap()
            .contains("no submission intent or thermal setter")
    );
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
    json!({"operation":"setFanOverride","version":80,"requestId":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"value":value,"settings":settings})
}
#[cfg(feature = "service-fan-mutation-v2")]
fn fan_rig(selector: u8, manual: u8) -> Rig {
    let files = MemoryFiles::default();
    files.profiles(1);
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
        ("version", json!(81)),
        ("requestId", json!("bad")),
        ("extra", json!(1)),
    ] {
        let mut v = valid.clone();
        v[key] = value;
        assert!(StatusRequest::decode(&serde_json::to_vec(&v).unwrap()).is_err());
    }
    for text in [
        r#"{"operation":"setFanOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000001"}"#,
        r#"{"operation":"setFanOverride","version":80,"requestId":"aaaaaaaa-bbbb-cccc-dddd-000000000001","value":{"value":1,"value":3,"lifetime":1}}"#,
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
        let r = fan_rig(selector, manual);
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
        drop(state);
        let public=r.request_as(json!({"operation":"getStatus","version":80,"requestId":"bbbbbbbb-cccc-dddd-eeee-000000000077"}),USER,"S-1-5-21-111-222-333-1002").await;
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
        r.get().await;
        let out = r.request(fan_request(102, Some(mode), 3700)).await;
        assert_eq!(
            out["result"]["status"],
            if selector == 0 && manual == 0 && mode == 0 {
                1
            } else {
                0
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
        assert_eq!(out["result"]["status"], 0);
        assert_eq!(r.port.state.lock().unwrap().writes, cut);
        let before = r.files.read("thermal-mutation.journal").unwrap();
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
            let end = Instant::now() + Duration::from_secs(2);
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
        &serde_json::to_vec(&fan_request(151, Some(1), 3700)).unwrap(),
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
    let end = Instant::now() + Duration::from_secs(2);
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
        tokio::time::timeout(Duration::from_secs(2), draining)
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
        let result = r.request(fan_request(171, Some(1), 3700)).await;
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

#[cfg(feature = "service-fan-mutation-v2")]
#[tokio::test]
async fn fan_v2_owned_limited_source_performance_stays_stored_without_consuming_release() {
    let r = fan_rig(0, 0);
    r.port.state.lock().unwrap().actual = 10;
    r.get().await;
    let applied = r.request(fan_request(180, Some(1), 3700)).await;
    assert_eq!(applied["result"]["status"], 1);
    let before = fan_rows(&r).len();
    let perf = r.set(181, value(1, 1)).await;
    assert_eq!(perf["result"]["status"], 0);
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

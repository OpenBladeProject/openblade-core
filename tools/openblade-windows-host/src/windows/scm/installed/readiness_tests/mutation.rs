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
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        Ok(self.bytes.lock().unwrap().get(name).cloned())
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
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
    fresh_refuse: AtomicBool,
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
            fresh_refuse: AtomicBool::new(false),
            activation_refusal: AtomicU8::new(0),
            lifecycle: Default::default(),
            activations: AtomicUsize::new(0),
            retired: AtomicBool::new(false),
        })
    }
}
impl ReadCustody for Custody {
    fn authorize(&self) -> Option<ReadAdmission> {
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
    fn authorize(&self) -> Option<ReadAdmission> {
        if self.fresh_refuse.load(Ordering::Acquire) {
            return None;
        }
        ReadCustody::authorize(self)
    }
}
struct Origin(Arc<crate::loaded_origin::lifetime::Lifetime>);
impl StatusOrigin for Origin {
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
            assert_eq!(entry["progress"], if number % 2 == 1 { 1 } else { 3 });
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
        let runtime = Runtime::injected(move |sid, session, _| {
            assert_eq!(sid, SID);
            assert_eq!(session, 7);
            Ok((storage.clone(), fresh.clone()))
        });
        let entered = runtime.entered.clone();
        let metadata = Arc::new(AtomicU8::new(0));
        let origin = Arc::new(crate::loaded_origin::lifetime::Lifetime::default());
        let source = injected_mutation_source(
            Arc::new(Origin(origin.clone())),
            MailboxSource::Reader(reader.clone()),
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
        }
    }
    async fn request(&self, request: Value) -> Value {
        self.request_as(request, USER, SID).await
    }
    async fn request_as(&self, request: Value, caller: Caller, sid: &str) -> Value {
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
    assert_eq!(status["status"]["desiredState"]["performance"]["source"], 2);
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
    lines[0]["progress"] = json!(1);
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

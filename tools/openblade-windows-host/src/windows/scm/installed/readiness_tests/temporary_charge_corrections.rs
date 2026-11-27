//! Injected actual dispatcher/Reader coverage; no Windows suppliers are run.
use super::*;
pub(super) struct CapturedFresh {
    custody: Arc<Custody>,
    session: u32,
    authentication: (u32, i32),
    proof: ReadAdmission,
}
impl CapturedFresh {
    pub(super) fn new(custody: Arc<Custody>, session: u32) -> io::Result<Self> {
        Ok(Self {
            session,
            authentication: Fresh::authentication(custody.as_ref()).unwrap(),
            proof: Fresh::authorize(custody.as_ref()).ok_or(io::ErrorKind::PermissionDenied)?,
            custody,
        })
    }
}
impl Fresh for CapturedFresh {
    fn on_battery(&self) -> Option<bool> {
        self.custody.on_battery()
    }
    // As `WindowsGuard::power_generation`, so a test that keeps this capture
    // through a resume observes the class a reconciliation reads.
    fn power_generation(&self) -> Option<u64> {
        Fresh::power_generation(self.custody.as_ref())
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        Some(self.authentication)
    }
    fn verify_health(&self) -> io::Result<()> {
        Fresh::verify_health(self.custody.as_ref())
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        (identity_holds(&self.custody, self.session, self.authentication)
            && Fresh::authorize(self.custody.as_ref()) == Some(self.proof))
        .then_some(self.proof)
    }
    #[cfg(feature = "service-profile-triggers-v1")]
    fn authorize_storage(&self) -> bool {
        identity_holds(&self.custody, self.session, self.authentication)
            && Fresh::authorize_storage(self.custody.as_ref())
    }
}
/// The captured session is still the console's and its logon is unchanged.
fn identity_holds(custody: &Custody, session: u32, authentication: (u32, i32)) -> bool {
    custody.current_session.load(Ordering::Acquire) == session as usize
        && custody.current_console.load(Ordering::Acquire)
        && Fresh::authentication(custody) == Some(authentication)
}
/// Test model of `WindowsGuard::capture_storage`: the session, console and
/// logon at capture, then the custody's storage admission, without the
/// lifecycle epoch. It authorizes no read or write.
#[cfg(feature = "service-profile-triggers-v1")]
pub(super) struct CapturedStorage {
    custody: Arc<Custody>,
    session: u32,
    authentication: (u32, i32),
}
#[cfg(feature = "service-profile-triggers-v1")]
impl CapturedStorage {
    pub(super) fn new(custody: Arc<Custody>, session: u32) -> io::Result<Self> {
        let captured = Self {
            session,
            authentication: Fresh::authentication(custody.as_ref()).unwrap(),
            custody,
        };
        if !captured.authorize_storage() {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        Ok(captured)
    }
}
#[cfg(feature = "service-profile-triggers-v1")]
impl Fresh for CapturedStorage {
    fn on_battery(&self) -> Option<bool> {
        self.custody.on_battery()
    }
    fn power_generation(&self) -> Option<u64> {
        Fresh::power_generation(self.custody.as_ref())
    }
    fn authentication(&self) -> Option<(u32, i32)> {
        Some(self.authentication)
    }
    fn verify_health(&self) -> io::Result<()> {
        Fresh::verify_health(self.custody.as_ref())
    }
    fn authorize(&self) -> Option<ReadAdmission> {
        None
    }
    fn authorize_storage(&self) -> bool {
        identity_holds(&self.custody, self.session, self.authentication)
            && Fresh::authorize_storage(self.custody.as_ref())
    }
}
#[cfg(feature = "service-temporary-charge-v1")]
mod cases {
    use super::*;
    fn epoch(r: &Rig) -> u64 {
        r.custody.lifecycle.active().unwrap()
    }
    fn power_event(r: &Rig) {
        r.custody.lifecycle.ingress(
            windows_sys::Win32::System::Services::SERVICE_CONTROL_POWEREVENT,
            10,
        );
    }
    async fn enable(r: &Rig, n: u32) -> Value {
        r.get().await;
        let response = r
            .request(battery_request(n, "setFullChargeOverride", json!(true)))
            .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        fan_rows(r).last().unwrap().clone()
    }
    fn origin(row: &Value) -> openblade_domain::temporary_charge_history::Origin {
        openblade_domain::temporary_charge_history::Origin {
            request: row["request"].as_str().unwrap().into(),
            intent: row["intent"].as_str().unwrap().into(),
        }
    }
    fn user(session: u32) -> Caller {
        Caller {
            pid: Some(43),
            session,
            control_session: Some(session),
            ..USER
        }
    }

    #[tokio::test]
    async fn tfc_correction_never_registered_repeat_preserves_actual_source_owner() {
        for current_epoch in [false, true] {
            for drop_future in [false, true] {
                let r = paused_rig(MemoryFiles::default());
                // Its jobs run on paused time under a held test clock (see `frozen`), so a
                // loaded host costs the tick and the requests no budget.
                frozen(&r.port, async {
                let a = enable(&r, 850).await;
                r.port.state.lock().unwrap().actual = 0;
                r.metadata.store(1, Ordering::Release);
                if current_epoch {
                    power_event(&r);
                }
                let sent = r.port.state.lock().unwrap().sent.len();
                let journal = r.files.read("thermal-mutation.journal").unwrap();
                if drop_future {
                    let pause = r.reader.registration_pause_for_tests();
                    pause.set(1);
                    let mut b = Box::pin(r.request(battery_request(
                        851,
                        "setFullChargeOverride",
                        json!(true),
                    )));
                    tokio::select! { _=&mut b=>panic!("B unexpectedly completed"), _=async {while pause.get()!=2 {tokio::task::yield_now().await;}}=>{} }
                    drop(b);
                    pause.set(0);
                } else {
                    let b = r
                        .request(battery_request(851, "setFullChargeOverride", json!(true)))
                        .await;
                    assert_eq!(b["result"]["status"], 2, "{b}");
                }
                assert_eq!(
                    r.port.state.lock().unwrap().sent.len(),
                    sent,
                    "B must not GET or SET"
                );
                assert_eq!(
                    r.files.read("thermal-mutation.journal").unwrap(),
                    journal,
                    "B must not prepare"
                );
                if !current_epoch {
                    power_event(&r);
                }
                // Eligibility is checked before another request can repair the state.
                assert!(r.runtime.temporary_event_needed(epoch(&r)).unwrap());
                r.source.lock().await.process_power_source().await.unwrap();
                let cancel = fan_rows(&r).last().unwrap().clone();
                assert_eq!(cancel["progress"], 72);
                assert_eq!(cancel["tfc"]["origin"]["request"], a["request"]);
                assert_eq!(cancel["tfc"]["ids"], a["tfc"]["cancel_ids"]);
                assert_eq!(cancel["caller_pid"], a["caller_pid"]);
                assert_eq!(r.port.state.lock().unwrap().writes, 2);
                let publication = r.source.lock().await.publications().unwrap();
                let bytes = crate::service_status::encode_status_event(
                    &publication.borrow().active.project_private(Some(SID)),
                )
                .unwrap();
                println!(
                    "NATIVE_THERMAL_MUTATION_RESPONSE {}",
                    std::str::from_utf8(&bytes).unwrap()
                );
                })
                .await;
                r.stop().await;
            }
        }
    }

    #[tokio::test]
    async fn tfc_correction_registered_uncertainty_keeps_original_capacity_and_fan_ids() {
        let r = fan_rig(2, 0);
        fresh_observe(&r).await;
        assert_eq!(
            r.request(fan_request(1, Some(1), 3900)).await["result"]["status"],
            1
        );
        let fan = fan_rows(&r).last().unwrap().clone();
        let a = enable(&r, 852).await;
        let quota = r.storage.temporary_capacity_for_tests();
        assert!(quota.0 > 0);
        let writes = r.port.state.lock().unwrap().writes;
        let release = Arc::new(AtomicBool::new(false));
        {
            let mut s = r.port.state.lock().unwrap();
            s.temporary_raw = 0;
            s.held_write = Some((writes + 1, release.clone()));
        }
        let mut b = Box::pin(r.request(battery_request(853, "setFullChargeOverride", json!(true))));
        tokio::select! { _=&mut b=>panic!("B completed"), _=async {while r.port.state.lock().unwrap().writes!=writes+1 {tokio::time::sleep(Duration::from_millis(1)).await;}}=>{} }
        drop(b);
        release.store(true, Ordering::Release);
        r.reader.drain_active().await.unwrap();
        assert!(r.storage.pending());
        let b = fan_rows(&r).last().unwrap().clone();
        let a_ids = a["tfc"]["ids"]
            .as_array()
            .unwrap()
            .iter()
            .chain(a["tfc"]["cancel_ids"].as_array().unwrap())
            .collect::<Vec<_>>();
        assert!(
            b["tfc"]["ids"]
                .as_array()
                .unwrap()
                .iter()
                .all(|id| !a_ids.contains(&id))
        );
        let fan_ids = ["preflight", "ids", "cleanup"]
            .into_iter()
            .flat_map(|key| fan["fan"][key].as_array().unwrap().iter())
            .flat_map(|pair| pair.as_array().unwrap().iter())
            .collect::<Vec<_>>();
        assert!(a_ids.iter().all(|id| !fan_ids.contains(id)));
        assert!(
            b["tfc"]["ids"]
                .as_array()
                .unwrap()
                .iter()
                .chain(b["tfc"]["cancel_ids"].as_array().unwrap())
                .all(|id| !fan_ids.contains(&id))
        );
        assert_ne!(b["tfc"]["fan_owner"], Value::Null);
        assert_eq!(r.storage.temporary_capacity_for_tests(), quota);
        r.storage.release_temporary_capacity(&origin(&a)).unwrap();
        assert_eq!(
            r.storage.temporary_capacity_for_tests(),
            quota,
            "pending quota retained"
        );
        r.port.state.lock().unwrap().actual = 0;
        r.metadata.store(1, Ordering::Release);
        power_event(&r);
        r.source.lock().await.process_power_source().await.unwrap();
        assert_eq!(
            r.port.state.lock().unwrap().writes,
            writes + 1,
            "no A fallback"
        );
        assert!(r.storage.outstanding_fan());
        assert_eq!(
            fan_rows(&r).iter().find(|x| x["progress"] == 30).unwrap()["fan"],
            fan["fan"]
        );
        assert!(
            r.source.lock().await.drain().await.is_err(),
            "unresolved work cannot claim fan handback"
        );
    }

    #[tokio::test]
    async fn tfc_correction_independent_false_and_replacement_release_only_matching_capacity() {
        let r = Rig::new(MemoryFiles::default());
        let a = enable(&r, 854).await;
        let b = enable(&r, 855).await;
        let quota = r.storage.temporary_capacity_for_tests();
        assert_eq!(quota.1, Some(origin(&b)));
        r.storage.release_temporary_capacity(&origin(&a)).unwrap();
        assert_eq!(
            r.storage.temporary_capacity_for_tests(),
            quota,
            "new owner quota survives old release"
        );
        r.port.state.lock().unwrap().temporary_raw = 0;
        let response = r
            .request(battery_request(
                856,
                "setBatteryPolicy",
                json!({"chargeLimitPercent":70}),
            ))
            .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        assert!(!r.runtime.temporary_event_needed(epoch(&r) + 1).unwrap());
        assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
        r.stop().await;
    }

    fn padded_history(target: usize) -> Vec<u8> {
        let mut bytes = Vec::new();
        let mut n = 200000;
        while bytes.len() + 900 < target {
            for progress in [0, 6] {
                let row = json!({"request":format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}"),"owner":"7".repeat(64),"user_sid":SID,"caller_pid":44,"session":7,"requested":null,"destination":1,"intent":format!("{:x}",<sha2::Sha256 as sha2::Digest>::digest(b"null")),"progress":progress});
                bytes.extend(serde_json::to_vec(&row).unwrap());
                bytes.push(b'\n');
            }
            n += 1;
        }
        let pad = target - bytes.len();
        bytes.splice(
            bytes.len() - 1..bytes.len() - 1,
            std::iter::repeat_n(b' ', pad),
        );
        bytes
    }
    #[tokio::test]
    async fn tfc_correction_settled_abandoned_quota_allows_roll_and_new_explicit_cancel() {
        let probe = paused_rig(MemoryFiles::default());
        enable(&probe, 857).await;
        let enable_len = probe
            .files
            .read("thermal-mutation.journal")
            .unwrap()
            .unwrap()
            .len();
        let quota = probe.storage.temporary_capacity_for_tests().0;
        probe.stop().await;
        // Leave less space than the old retained quota would consume. A complete
        // settled operation may roll only after that exact obligation is abandoned.
        let target = crate::thermal_history::SEGMENT - enable_len - quota - 400;
        let files = MemoryFiles::default();
        files.roll_settled.store(true, Ordering::Release);
        files
            .bytes
            .lock()
            .unwrap()
            .insert("thermal-mutation.journal".into(), padded_history(target));
        // The padded journal makes each append cost real CPU, which paused time
        // does not charge to a job (`paused_rig`).
        let r = paused_rig(files);
        let a = enable(&r, 858).await;
        r.port.state.lock().unwrap().actual = 0;
        r.metadata.store(1, Ordering::Release);
        r.custody.fresh_refuse.store(true, Ordering::Release);
        power_event(&r);
        r.process_power_source().await.unwrap();
        assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        r.custody.fresh_refuse.store(false, Ordering::Release);
        power_event(&r);
        r.process_power_source().await.unwrap();
        assert_eq!(
            r.port.state.lock().unwrap().writes,
            1,
            "no automatic replay"
        );
        r.port.state.lock().unwrap().actual = 15;
        let response = r.set(2, value(1, 1)).await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        // The abandoned reservation also covered a fresh-ID retry, so one
        // ordinary operation no longer reaches the boundary. Settled
        // operations continue until one rolls.
        for n in 3.. {
            if r.files.read("retained-segment").unwrap().is_some() {
                break;
            }
            assert!(n < 12, "settled operations never reached the boundary");
            let response = r.set(n, value(1 + i32::from(n % 2), 1)).await;
            assert_eq!(response["result"]["status"], 1, "{response}");
        }
        let response = r
            .request(battery_request(859, "setFullChargeOverride", json!(false)))
            .await;
        assert_eq!(response["result"]["status"], 1, "{response}");
        assert_eq!(r.port.state.lock().unwrap().temporary_raw, 0);
        let cancel = fan_rows(&r).last().unwrap().clone();
        assert_eq!(cancel["tfc"]["origin"], Value::Null);
        assert_ne!(cancel["tfc"]["ids"], a["tfc"]["cancel_ids"]);
        assert!(
            r.files.read("retained-segment").unwrap().is_some(),
            "settled journal rolled without rewriting prior rows"
        );
        r.stop().await;
    }

    #[tokio::test]
    async fn tfc_correction_same_sid_current_session_cancel_has_new_identity_and_proof() {
        for session in [7, 8] {
            for already_false in [false, true] {
                let r = Rig::new(MemoryFiles::default());
                r.custody.scoped_fresh.store(true, Ordering::Release);
                let a = enable(&r, 860).await;
                r.custody
                    .current_session
                    .store(session as usize, Ordering::Release);
                r.custody.authentication.store(2, Ordering::Release);
                power_event(&r);
                if already_false {
                    r.port.state.lock().unwrap().temporary_raw = 0;
                }
                let response = r
                    .request_as(
                        battery_request(861, "setFullChargeOverride", json!(false)),
                        user(session),
                        SID,
                    )
                    .await;
                assert_eq!(response["result"]["status"], 1, "{response}");
                let cancel = fan_rows(&r).last().unwrap().clone();
                assert_eq!(cancel["session"], session);
                assert_eq!(cancel["caller_pid"], 43);
                assert_eq!(cancel["tfc"]["origin"], Value::Null);
                assert_ne!(cancel["request"], a["request"]);
                assert_ne!(cancel["tfc"]["ids"], a["tfc"]["cancel_ids"]);
                assert_eq!(cancel["progress"], if already_false { 73 } else { 72 });
                assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
                assert!(!r.runtime.temporary_event_needed(epoch(&r)).unwrap());
                assert_eq!(
                    r.port.state.lock().unwrap().writes,
                    if already_false { 1 } else { 2 }
                );
                r.stop().await;
            }
        }
    }

    #[tokio::test]
    async fn tfc_correction_explicit_recovery_keeps_authority_flags_and_fan_fences() {
        for fault in 0..7 {
            let r = if fault == 6 {
                let r = fan_rig(2, 0);
                fresh_observe(&r).await;
                assert_eq!(
                    r.request(fan_request(1, Some(1), 3900)).await["result"]["status"],
                    1
                );
                r
            } else {
                Rig::new(MemoryFiles::default())
            };
            r.custody.scoped_fresh.store(true, Ordering::Release);
            enable(&r, 862).await;
            let writes = r.port.state.lock().unwrap().writes;
            r.custody.current_session.store(8, Ordering::Release);
            r.custody.authentication.store(2, Ordering::Release);
            power_event(&r);
            let mut caller = user(8);
            let mut sid = SID;
            match fault {
                0 => sid = "S-1-5-21-111-222-333-1002",
                1 => caller = user(9),
                2 => r.custody.current_console.store(false, Ordering::Release),
                3 => r.port.state.lock().unwrap().temporary_raw = 255,
                4 => r.port.state.lock().unwrap().temporary_raw = 5,
                5 => {
                    let c = r.custody.clone();
                    r.port.state.lock().unwrap().battery_getter_hook =
                        Some(Arc::new(move |_, _| {
                            c.generation.store(8, Ordering::Release);
                        }));
                }
                _ => {}
            }
            let response = r
                .request_as(
                    battery_request(863, "setFullChargeOverride", json!(false)),
                    caller,
                    sid,
                )
                .await;
            assert_eq!(response["result"]["status"], 2, "fault {fault}: {response}");
            assert_eq!(r.port.state.lock().unwrap().writes, writes, "fault {fault}");
            if fault == 6 {
                assert!(
                    r.source.lock().await.drain().await.is_err(),
                    "old-session fan cleanup stays fenced"
                );
            } else {
                r.stop().await;
            }
        }
        // Old-LUID automatic authority is still invalid, even for the same session.
        let r = Rig::new(MemoryFiles::default());
        r.custody.scoped_fresh.store(true, Ordering::Release);
        enable(&r, 864).await;
        r.custody.authentication.store(2, Ordering::Release);
        r.port.state.lock().unwrap().actual = 0;
        r.metadata.store(1, Ordering::Release);
        power_event(&r);
        r.source.lock().await.process_power_source().await.unwrap();
        assert_eq!(r.port.state.lock().unwrap().writes, 1);
        r.stop().await;
    }

    #[tokio::test]
    async fn tfc_correction_capacity_read_error_retains_release_only_origin_without_replay() {
        let r = Rig::new(MemoryFiles::default());
        let a = enable(&r, 865).await;
        r.port.state.lock().unwrap().actual = 0;
        r.metadata.store(1, Ordering::Release);
        r.custody.fresh_refuse.store(true, Ordering::Release);
        r.files.fail_quota_read.store(true, Ordering::Release);
        power_event(&r);
        assert!(
            r.source.lock().await.process_power_source().await.is_err(),
            "real I/O failure propagates"
        );
        assert_eq!(r.storage.temporary_capacity_for_tests().1, Some(origin(&a)));
        r.files.fail_quota_read.store(false, Ordering::Release);
        r.custody.fresh_refuse.store(false, Ordering::Release);
        power_event(&r);
        r.source.lock().await.process_power_source().await.unwrap();
        assert_eq!(r.storage.temporary_capacity_for_tests(), (0, None));
        assert_eq!(
            r.port.state.lock().unwrap().writes,
            1,
            "new epoch retries bookkeeping only"
        );
        r.stop().await;
    }
}

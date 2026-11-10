use std::cell::{Cell, RefCell};

use windows_sys::Win32::{
    Foundation::NO_ERROR,
    System::Services::{SERVICE_CONTROL_SHUTDOWN, SERVICE_CONTROL_STOP},
};

use super::{super::Controls, *};

fn send(callbacks: &Callbacks, code: u32, event: u32, now: Instant) {
    callbacks.control(code, event, None, now);
}

#[tokio::test]
async fn coalesces_storm_and_reads_fresh_console_and_power() {
    let (worker, callbacks) = Worker::new();
    let (stop, shutdown) = oneshot::channel();
    let mut stop = Some(stop);
    let now = Instant::now();
    let mut work = Vec::new();
    let missing = Observation::from_windows(Some(255), u32::MAX);
    let known = Observation::from_windows(Some(1), 19);
    let mut reads = [missing, known].into_iter();

    worker
        .run(
            shutdown,
            || reads.next().unwrap(),
            async |item| {
                work.push(item);
                match item {
                    Work::Refresh(Activity::Unknown, observation) if observation == missing => {
                        for _ in 0..10_000 {
                            send(
                                &callbacks,
                                SERVICE_CONTROL_SESSIONCHANGE,
                                WTS_CONSOLE_CONNECT,
                                now,
                            );
                            send(&callbacks, SERVICE_CONTROL_POWEREVENT, PBT_APMSUSPEND, now);
                            send(
                                &callbacks,
                                SERVICE_CONTROL_POWEREVENT,
                                PBT_APMRESUMEAUTOMATIC,
                                now,
                            );
                        }
                    }
                    Work::Refresh(Activity::Active, observation) if observation == known => {
                        stop.take().unwrap().send(()).unwrap();
                    }
                    _ => {}
                }
                Ok(())
            },
        )
        .await
        .unwrap();
    assert_eq!(
        missing,
        Observation {
            power: PowerSource::Unknown,
            console_session: None
        }
    );
    assert_eq!(
        known,
        Observation {
            power: PowerSource::ExternalUnknown,
            console_session: Some(19)
        }
    );
    assert_eq!(
        work,
        [
            Work::Refresh(Activity::Unknown, missing),
            Work::Invalidate(Activity::Active),
            Work::Refresh(Activity::Active, known),
            Work::Stop,
        ]
    );
}

#[tokio::test]
async fn suspend_fences_observations_and_display_can_recover_a_missed_resume() {
    let (worker, callbacks) = Worker::new();
    let (stop, shutdown) = oneshot::channel();
    let mut stop = Some(stop);
    let now = Instant::now();
    let work = RefCell::new(Vec::new());
    let reads = Cell::new(0);
    let display = |value| {
        callbacks.control(
            SERVICE_CONTROL_POWEREVENT,
            PBT_POWERSETTINGCHANGE,
            Some(PowerSetting::Display(value)),
            now,
        )
    };
    let evidence = Observation::from_windows(None, u32::MAX);

    worker
        .run(
            shutdown,
            || {
                reads.set(reads.get() + 1);
                if reads.get() == 1 {
                    display(1);
                    display(0);
                    assert_eq!(work.borrow().len(), 0);
                    send(&callbacks, SERVICE_CONTROL_POWEREVENT, PBT_APMSUSPEND, now);
                    send(
                        &callbacks,
                        SERVICE_CONTROL_POWEREVENT,
                        PBT_APMPOWERSTATUSCHANGE,
                        now,
                    );
                }
                evidence
            },
            async |item| {
                work.borrow_mut().push(item);
                match item {
                    Work::Invalidate(Activity::Suspended) => {
                        assert_eq!(reads.get(), 1);
                        display(9);
                        assert_eq!(
                            callbacks.0.upgrade().unwrap().state().activity,
                            Activity::Suspended
                        );
                        display(2);
                    }
                    Work::Refresh(Activity::Active, _) => {
                        stop.take().unwrap().send(()).unwrap();
                    }
                    _ => {}
                }
                Ok(())
            },
        )
        .await
        .unwrap();
    assert_eq!(reads.get(), 2);
    assert_eq!(
        *work.borrow(),
        [
            Work::Invalidate(Activity::Suspended),
            Work::Refresh(Activity::Active, evidence),
            Work::Stop
        ]
    );
}

#[test]
fn resume_window_rearms_on_suspend_and_ignores_unrelated_callbacks() {
    let (worker, callbacks) = Worker::new();
    let now = Instant::now();
    let power = |event, time| send(&callbacks, SERVICE_CONTROL_POWEREVENT, event, time);
    power(PBT_APMRESUMEAUTOMATIC, now);
    assert!(std::mem::take(&mut worker.0.state().pending).refresh);
    power(PBT_APMRESUMESUSPEND, now + DUPLICATE_RESUME_WINDOW);
    power(6, now); // PBT_APMRESUMECRITICAL is outside the managed contract.
    send(&callbacks, SERVICE_CONTROL_SESSIONCHANGE, u32::MAX, now);
    assert!(!worker.0.state().pending.refresh);
    assert!(!worker.0.state().pending.invalidate);
    power(
        PBT_APMRESUMESUSPEND,
        now + DUPLICATE_RESUME_WINDOW + Duration::from_millis(1),
    );
    assert!(std::mem::take(&mut worker.0.state().pending).refresh);
    power(PBT_APMSUSPEND, now + Duration::from_secs(6));
    power(PBT_APMRESUMEAUTOMATIC, now + Duration::from_secs(6));
    let state = worker.0.state();
    assert_eq!(state.activity, Activity::Active);
    assert!(state.pending.invalidate && state.pending.refresh);
}

#[tokio::test]
async fn scm_stop_drops_pending_refresh_and_awaits_serial_retirement() {
    let (worker, callbacks) = Worker::new();
    let controls = Controls::new();
    let shutdown = controls.arm();
    let (refreshing, wait_refreshing) = oneshot::channel();
    let (finish_refresh, wait_finish_refresh) = oneshot::channel();
    let mut refreshing = Some(refreshing);
    let mut wait_finish_refresh = Some(wait_finish_refresh);
    let (retiring, wait_retiring) = oneshot::channel();
    let (retired, wait_retired) = oneshot::channel();
    let mut retiring = Some(retiring);
    let mut wait_retired = Some(wait_retired);
    let work = RefCell::new(Vec::new());
    let now = Instant::now();

    let service = worker.run(
        shutdown,
        || Observation::from_windows(Some(0), 0),
        async |item| {
            work.borrow_mut().push(item);
            match item {
                Work::Refresh(..) => {
                    send(
                        &callbacks,
                        SERVICE_CONTROL_SESSIONCHANGE,
                        WTS_SESSION_LOCK,
                        now,
                    );
                    refreshing.take().unwrap().send(()).unwrap();
                    wait_finish_refresh.take().unwrap().await.unwrap();
                }
                Work::Stop => {
                    retiring.take().unwrap().send(()).unwrap();
                    wait_retired.take().unwrap().await.unwrap();
                }
                _ => {}
            }
            Ok(())
        },
    );
    let late = async {
        wait_refreshing.await.unwrap();
        assert_eq!(controls.handle(SERVICE_CONTROL_STOP), NO_ERROR);
        tokio::task::yield_now().await;
        assert_eq!(work.borrow().len(), 1);
        finish_refresh.send(()).unwrap();
        wait_retiring.await.unwrap();
        assert!(!callbacks.0.upgrade().unwrap().state().accepting);
        send(
            &callbacks,
            SERVICE_CONTROL_POWEREVENT,
            PBT_APMRESUMEAUTOMATIC,
            now,
        );
        assert_eq!(
            callbacks.0.upgrade().unwrap().state().activity,
            Activity::Unknown
        );
        assert_eq!(controls.handle(SERVICE_CONTROL_SHUTDOWN), NO_ERROR);
        retired.send(()).unwrap();
    };
    let (result, ()) = tokio::time::timeout(Duration::from_secs(2), async {
        tokio::join!(service, late)
    })
    .await
    .unwrap();
    result.unwrap();
    assert_eq!(
        *work.borrow(),
        [
            Work::Refresh(
                Activity::Unknown,
                Observation {
                    power: PowerSource::Battery,
                    console_session: Some(0)
                }
            ),
            Work::Stop,
        ]
    );
    assert!(callbacks.0.upgrade().is_none());
    send(
        &callbacks,
        SERVICE_CONTROL_SESSIONCHANGE,
        WTS_SESSION_UNLOCK,
        now,
    );
}

#[tokio::test]
async fn refresh_failure_still_retires_and_preexisting_stop_skips_observation() {
    for stop_first in [false, true] {
        let (worker, _) = Worker::new();
        let (stop, shutdown) = oneshot::channel();
        if stop_first {
            stop.send(()).unwrap();
        }
        let mut stopped = false;
        let result = worker
            .run(
                shutdown,
                || {
                    assert!(!stop_first);
                    Observation::from_windows(None, u32::MAX)
                },
                async |item| {
                    if item == Work::Stop {
                        stopped = true;
                        return Ok(());
                    }
                    Err(io::Error::other("scripted refresh failure"))
                },
            )
            .await;
        assert!(stopped);
        assert_eq!(result.is_ok(), stop_first);
    }
}

#[tokio::test]
async fn power_hint_replaces_known_evidence_with_unknown() {
    let (worker, callbacks) = Worker::new();
    let (stop, shutdown) = oneshot::channel();
    let mut stop = Some(stop);
    let known = Observation::from_windows(Some(0), 23);
    let missing = Observation::from_windows(None, u32::MAX);
    let hint = || {
        callbacks.control(
            SERVICE_CONTROL_POWEREVENT,
            PBT_POWERSETTINGCHANGE,
            Some(PowerSetting::Source),
            Instant::now(),
        )
    };
    let stale = Observation::from_windows(Some(1), 23);
    let mut reads = [known, stale, missing].into_iter();
    let mut work = Vec::new();

    tokio::time::timeout(
        Duration::from_secs(2),
        worker.run(
            shutdown,
            || {
                let evidence = reads.next().unwrap();
                if evidence == stale {
                    hint();
                }
                evidence
            },
            async |item| {
                work.push(item);
                match item {
                    Work::Refresh(_, evidence) if evidence == known => hint(),
                    Work::Refresh(..) => stop.take().unwrap().send(()).unwrap(),
                    _ => {}
                }
                Ok(())
            },
        ),
    )
    .await
    .unwrap()
    .unwrap();

    assert_eq!(
        work,
        [
            Work::Refresh(Activity::Unknown, known),
            Work::Refresh(Activity::Unknown, missing),
            Work::Stop,
        ]
    );
}

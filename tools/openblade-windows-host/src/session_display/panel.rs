use std::{
    thread,
    time::{Duration, Instant},
};

use openblade_platform_windows::display::{
    DisplayReadError, InternalDisplayRefresh,
    scripted::{RefreshTiming, Scenario},
};

use super::{HdrFixture, RefreshFixture};

#[derive(Clone, Copy, PartialEq, Eq)]
pub(super) struct Context {
    pub(super) power_source: u8,
    generation: u64,
}

pub(super) struct ScriptedDisplay {
    pub(super) scenario: Scenario,
    pub(super) hdr_fixture: HdrFixture,
    pub(super) refresh_fixture: RefreshFixture,
    pub(super) enabled: bool,
    pub(super) timing: RefreshTiming,
}

pub(super) type Outcome = (i32, Option<String>, bool);

impl ScriptedDisplay {
    pub(super) fn new(
        scenario: Scenario,
        hdr_fixture: HdrFixture,
        refresh_fixture: RefreshFixture,
    ) -> Self {
        Self {
            scenario,
            hdr_fixture,
            refresh_fixture,
            enabled: true,
            timing: RefreshTiming::default(),
        }
    }

    pub(super) fn read(&self, replaced: bool) -> Result<InternalDisplayRefresh, DisplayReadError> {
        self.scenario
            .read_with_hdr(self.enabled, self.timing, replaced)
    }

    // This is synthetic session and power evidence only. Pipe token identity
    // does not prove a real foreground console or a confirmed power source.
    pub(super) fn context(&self, fresh: bool) -> Option<Context> {
        (self.hdr_fixture != HdrFixture::Inactive
            && self.refresh_fixture != RefreshFixture::Inactive)
            .then_some(Context {
                power_source: 0, // PluggedIn.
                generation: u64::from(
                    fresh
                        && (self.hdr_fixture == HdrFixture::StalePower
                            || self.refresh_fixture == RefreshFixture::StalePower),
                ),
            })
    }
}

pub(super) fn wait_for_disconnect(signal: &str, cancelled: &dyn Fn() -> bool) {
    println!("{signal}");
    let deadline = Instant::now() + Duration::from_secs(2);
    while !cancelled() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(5));
    }
}

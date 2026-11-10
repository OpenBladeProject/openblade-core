//! In-memory WCS observations and effects for the private display replay.

use std::ffi::{OsStr, OsString};

use super::{
    ActiveInternalDisplay, ColorProfileSnapshot, HdrState, ProfileRead, ProfileReadError,
    ProfileReads, ProfileScope, snapshot,
    transaction::{
        CurrentUserChange, Monitor, ScriptedOperations, SetColorProfile, set_color_profile,
    },
};
use crate::display::{DisplayReadError, InternalDisplayTopology, scripted::MONITOR_PATH};

pub use super::transaction::{Disposition, Failure, Outcome, Rejected};

const FACTORY: &str = "Synthetic Factory.icm";
const NATIVE: &str = "Native.icm";
const KEY: &str = "Synthetic monitor key";
const READ_ERROR: i32 = -2_147_024_891;

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum WcsFixture {
    Available,
    Unavailable,
    FailedWithoutEffect,
    FailedWithEffect,
    MismatchedReadback,
}

pub struct Profiles {
    fixture: WcsFixture,
    scope: ProfileScope,
    user_profiles: Vec<OsString>,
    user_default: Option<OsString>,
}

impl Profiles {
    pub fn new(fixture: WcsFixture) -> Self {
        Self {
            fixture,
            scope: ProfileScope::SystemWide,
            user_profiles: Vec::new(),
            user_default: None,
        }
    }

    pub fn query(
        &mut self,
        display: &ActiveInternalDisplay,
    ) -> Result<ColorProfileSnapshot, ProfileReadError> {
        snapshot(self, display, hdr_preference(display))
    }

    pub fn set(
        &mut self,
        key: &str,
        topology: impl FnMut() -> Result<InternalDisplayTopology, DisplayReadError>,
        admitted: &impl Fn() -> bool,
    ) -> Result<Outcome, Rejected> {
        set_color_profile(
            SetColorProfile(OsStr::new(key)),
            self,
            topology,
            hdr_preference,
            admitted,
        )
    }
}

fn hdr_preference(display: &ActiveInternalDisplay) -> HdrState {
    match &display.advanced_color {
        Ok(hdr) if hdr.hdr_user_enabled => HdrState::On,
        Ok(_) => HdrState::Off,
        Err(_) => HdrState::Unknown,
    }
}

impl ProfileReads for Profiles {
    fn scope(&mut self, _: &ActiveInternalDisplay) -> Result<ProfileScope, ProfileReadError> {
        if self.fixture == WcsFixture::Unavailable {
            return Err(ProfileReadError::Native {
                operation: ProfileRead::Scope,
                hresult: READ_ERROR,
            });
        }
        Ok(self.scope)
    }

    fn associations(
        &mut self,
        _: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Vec<OsString>, ProfileReadError> {
        Ok(match scope {
            ProfileScope::SystemWide => vec![FACTORY.into()],
            ProfileScope::CurrentUser => self.user_profiles.clone(),
        })
    }

    fn default_profile(
        &mut self,
        _: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Option<OsString>, ProfileReadError> {
        Ok(match scope {
            ProfileScope::SystemWide => Some(FACTORY.into()),
            ProfileScope::CurrentUser => self.user_default.clone(),
        })
    }

    fn presets(&mut self) -> Vec<OsString> {
        vec![NATIVE.into()]
    }
}

impl ScriptedOperations for Profiles {
    fn monitors(&mut self, _: &ActiveInternalDisplay) -> Result<Vec<Monitor>, Failure> {
        Ok(vec![Monitor {
            interface: MONITOR_PATH.into(),
            key: KEY.into(),
            active: true,
        }])
    }

    fn submit(
        &mut self,
        _: &ActiveInternalDisplay,
        monitor_key: &OsStr,
        change: CurrentUserChange<'_>,
    ) -> Result<(), Failure> {
        assert_eq!(monitor_key, KEY);
        match change {
            CurrentUserChange::Associate(profile) => self.user_profiles.push(profile.into()),
            CurrentUserChange::Remove(profile) => {
                self.user_profiles.retain(|id| id != profile);
                if self.user_default.as_deref() == Some(profile) {
                    self.user_default = None;
                }
            }
            CurrentUserChange::SelectScope(scope) => self.scope = scope,
            CurrentUserChange::SetDefault(profile) => {
                if self.fixture == WcsFixture::FailedWithoutEffect {
                    return Err(Failure::Submit(READ_ERROR));
                }
                self.user_default = Some(if self.fixture == WcsFixture::MismatchedReadback {
                    "Unrelated.icm".into()
                } else {
                    profile.into()
                });
                if self.fixture == WcsFixture::FailedWithEffect {
                    return Err(Failure::Submit(READ_ERROR));
                }
            }
        }
        Ok(())
    }
}

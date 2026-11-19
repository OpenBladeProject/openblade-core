//! In-memory WCS observations and effects for the private display replay.

use std::ffi::{OsStr, OsString};

use super::{
    ActiveInternalDisplay, ColorProfileSnapshot, HdrState, Monitor, ProfileRead, ProfileReadError,
    ProfileReads, ProfileScope,
    native::ordinal_ignore_case,
    profiles_match, snapshot,
    transaction::{
        CurrentUserChange, ScriptedOperations, SetColorProfile, monitor_key, same_effect_name,
        set_color_profile,
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
    FailedAssociationWithEffect,
    MismatchedReadback,
    StartsApplied,
    StartsWithPartialAssociation,
    StartsWithDifferentScope,
    StartsWithDifferentAssociations,
}

/// A complete standard ICC observation for one scripted monitor under SDR.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct WcsEvidence {
    pub source_name: String,
    pub monitor_key: String,
    pub hdr_enabled: bool,
    pub current_user: bool,
    pub user_default: Option<String>,
    pub user_associations: Vec<String>,
    pub system_default: Option<String>,
    pub system_associations: Vec<String>,
}

impl WcsEvidence {
    pub fn after_selection(&self, profile: &str) -> Self {
        let mut after = self.clone();
        let profile_name = OsStr::new(profile);
        if !after
            .user_associations
            .iter()
            .any(|id| profiles_match(OsStr::new(id), profile_name))
            && !after
                .user_default
                .as_ref()
                .is_some_and(|id| same_effect_name(profile_name, OsStr::new(id)))
        {
            after.user_associations.push(profile.into());
            sort_associations(&mut after.user_associations);
        }
        after.current_user = true;
        after.user_default = Some(profile.into());
        after
    }
}

fn sort_associations(associations: &mut [String]) {
    associations.sort_by(|a, b| ordinal_ignore_case(OsStr::new(a), OsStr::new(b)));
}

pub struct Profiles {
    fixture: WcsFixture,
    scope: ProfileScope,
    user_profiles: Vec<OsString>,
    user_default: Option<OsString>,
}

impl Profiles {
    pub fn new(fixture: WcsFixture) -> Self {
        let applied = matches!(
            fixture,
            WcsFixture::StartsApplied
                | WcsFixture::StartsWithDifferentScope
                | WcsFixture::StartsWithDifferentAssociations
        );
        let mut profiles = Self {
            fixture,
            scope: ProfileScope::SystemWide,
            user_profiles: Vec::new(),
            user_default: None,
        };
        if applied || fixture == WcsFixture::StartsWithPartialAssociation {
            profiles.user_profiles.push(NATIVE.into());
        }
        if applied {
            profiles.user_default = Some(NATIVE.into());
            if fixture != WcsFixture::StartsWithDifferentScope {
                profiles.scope = ProfileScope::CurrentUser;
            }
            if fixture == WcsFixture::StartsWithDifferentAssociations {
                profiles.user_profiles.push("Unrelated.icm".into());
            }
        }
        profiles
    }

    pub fn evidence(&mut self, display: &ActiveInternalDisplay) -> Result<WcsEvidence, Failure> {
        let hdr = HdrState::of(display);
        if hdr != HdrState::Off {
            return Err(Failure::Hdr(hdr));
        }
        let text = |value: OsString| value.into_string().map_err(|_| Failure::Readback);
        let scope = self.scope(display)?;
        let mut read_scope = |scope| {
            let default = self
                .default_profile(display, scope)?
                .map(text)
                .transpose()?;
            let mut associations = self
                .associations(display, scope)?
                .into_iter()
                .map(text)
                .collect::<Result<Vec<_>, _>>()?;
            sort_associations(&mut associations);
            Ok::<_, Failure>((default, associations))
        };
        let (user_default, user_associations) = read_scope(ProfileScope::CurrentUser)?;
        let (system_default, system_associations) = read_scope(ProfileScope::SystemWide)?;
        let key = text(monitor_key(self, display)?)?;
        if self.scope(display)? != scope {
            return Err(Failure::StateChanged);
        }
        Ok(WcsEvidence {
            source_name: text(display.source_name.clone())?,
            monitor_key: key,
            hdr_enabled: false,
            current_user: scope == ProfileScope::CurrentUser,
            user_default,
            user_associations,
            system_default,
            system_associations,
        })
    }

    pub fn query(
        &mut self,
        display: &ActiveInternalDisplay,
    ) -> Result<ColorProfileSnapshot, ProfileReadError> {
        snapshot(self, display, HdrState::of(display))
    }

    pub fn set(
        &mut self,
        key: &str,
        topology: impl FnMut() -> Result<InternalDisplayTopology, DisplayReadError>,
        admitted: &dyn Fn() -> bool,
        prepare: impl FnOnce(WcsEvidence, String) -> Result<(), Failure>,
    ) -> Result<Outcome, Rejected> {
        set_color_profile(
            SetColorProfile(OsStr::new(key)),
            self,
            topology,
            HdrState::of,
            admitted,
            |profiles, display, profile| {
                let canonical = profile.to_str().ok_or(Failure::ProfileUnavailable)?;
                prepare(profiles.evidence(display)?, canonical.into())
            },
        )
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
            ProfileScope::SystemWide if self.fixture == WcsFixture::StartsWithDifferentScope => {
                vec![NATIVE.into()]
            }
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
            ProfileScope::SystemWide if self.fixture == WcsFixture::StartsWithDifferentScope => {
                Some(NATIVE.into())
            }
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
            CurrentUserChange::Associate(profile) => {
                self.user_profiles.push(profile.into());
                if self.fixture == WcsFixture::FailedAssociationWithEffect {
                    return Err(Failure::Submit(READ_ERROR));
                }
            }
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

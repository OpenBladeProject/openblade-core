//! The `profiles` member of `updateProfiles`.
//!
//! serde only steps over the member. The typed set is bound from the frame
//! bytes by the domain reader, which keeps the member order and number text
//! that managed's `System.Text.Json` binding depends on and serde drops.
use serde::{Deserialize, Deserializer, Serialize, Serializer, de::IgnoredAny};

#[cfg(feature = "service-profiles-v1")]
use openblade_domain::profile_set::{ProfileSet, encode, json, update};
#[cfg(feature = "service-profiles-v1")]
use std::sync::Arc;

#[derive(Clone, Default)]
pub(crate) struct ProfilesBody(#[cfg(feature = "service-profiles-v1")] Option<Arc<ProfileSet>>);

impl<'de> Deserialize<'de> for ProfilesBody {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        IgnoredAny::deserialize(deserializer).map(|_| Self::default())
    }
}

impl Serialize for ProfilesBody {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        #[cfg(feature = "service-profiles-v1")]
        if let Some(profiles) = &self.0 {
            let bytes = json::write(&encode::profile_set(profiles), false);
            return serde_json::from_slice::<serde_json::Value>(&bytes)
                .map_err(serde::ser::Error::custom)?
                .serialize(serializer);
        }
        serializer.serialize_unit()
    }
}

#[cfg(feature = "service-profiles-v1")]
impl ProfilesBody {
    /// Binds the set as managed's IPC reader does and admits it as its body
    /// validation does. Duplicate executable paths are compared by Unicode
    /// upper case here; the stored-intent validator repeats the check with
    /// the operating system's ordinal comparison before anything is saved.
    pub(crate) fn bind(payload: &[u8]) -> std::io::Result<Self> {
        fn same_path(left: &str, right: &str) -> bool {
            left.to_uppercase() == right.to_uppercase()
        }
        update::decode_request(payload, same_path)
            .map(|profiles| Self(Some(Arc::new(profiles))))
            .map_err(|_| crate::service_frame::invalid("invalid Service profile set"))
    }

    pub(crate) fn profiles(&self) -> Option<&Arc<ProfileSet>> {
        self.0.as_ref()
    }
}

#[cfg(test)]
impl ProfilesBody {
    /// A set every lane can frame: the managed safe defaults where this
    /// composition binds profiles, and no body where it does not.
    pub(crate) fn sample() -> Self {
        #[cfg(feature = "service-profiles-v1")]
        return Self(Some(Arc::new(ProfileSet::safe_defaults())));
        #[cfg(not(feature = "service-profiles-v1"))]
        Self::default()
    }
}

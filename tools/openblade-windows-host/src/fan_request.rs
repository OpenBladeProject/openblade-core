//! Managed ManualOverride<FanMode> shape. Fixed=1, Maximum=3.
#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct FanOverride {
    pub value: u8,
    pub lifetime: u8,
}

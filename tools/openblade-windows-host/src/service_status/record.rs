//! The open records of a status snapshot (`FeatureRecord`): members native
//! keeps as JSON without a typed model, such as the confirmed state, the
//! profiles' and overrides' lighting, and every member a newer managed
//! status adds.
//!
//! A record is written with `kind` first in every object it holds
//! (`openblade_domain::kind_first`): managed binds the lighting a status
//! carries with its polymorphic `LightingParameters`, which needs its `kind`
//! first, and serde_json alone keeps an object's members sorted.
use openblade_domain::kind_first;
use serde::{Deserialize, Serialize, Serializer};
use serde_json::{Map, Value};
use std::ops::{Deref, DerefMut};

/// One open record of a status snapshot, written with `kind` first in each
/// object it holds.
#[derive(Clone, Debug, Default, PartialEq, Deserialize)]
#[serde(transparent)]
pub(crate) struct FeatureRecord(Map<String, Value>);

impl Deref for FeatureRecord {
    type Target = Map<String, Value>;
    fn deref(&self) -> &Self::Target {
        &self.0
    }
}
impl DerefMut for FeatureRecord {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}
impl From<Map<String, Value>> for FeatureRecord {
    fn from(members: Map<String, Value>) -> Self {
        Self(members)
    }
}
impl FromIterator<(String, Value)> for FeatureRecord {
    fn from_iter<I: IntoIterator<Item = (String, Value)>>(members: I) -> Self {
        Self(Map::from_iter(members))
    }
}
impl From<FeatureRecord> for Value {
    fn from(record: FeatureRecord) -> Self {
        Value::Object(record.0)
    }
}
impl Serialize for FeatureRecord {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        kind_first::object(&self.0, serializer)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// Managed binds lit parameters only with `kind` first, at every depth a
    /// record holds them, inside arrays too; other members keep their order.
    #[test]
    fn kind_is_written_first_in_every_object() {
        let lit = json!({"color": {"blue": 5, "green": 10, "red": 200}, "kind": "static"});
        let record: FeatureRecord = json!({
            "lighting": {"brightness": 10, "parameters": lit},
            "rules": [{"parameters": lit}],
            "kind": 3,
        })
        .as_object()
        .cloned()
        .unwrap()
        .into();
        let parameters = r#"{"kind":"static","color":{"blue":5,"green":10,"red":200}}"#;
        assert_eq!(
            serde_json::to_string(&record).unwrap(),
            format!(
                r#"{{"kind":3,"lighting":{{"brightness":10,"parameters":{parameters}}},"rules":[{{"parameters":{parameters}}}]}}"#
            )
        );
        // The record reads back unchanged.
        let bytes = serde_json::to_vec(&record).unwrap();
        assert_eq!(
            serde_json::from_slice::<FeatureRecord>(&bytes).unwrap(),
            record
        );
    }

    /// A flattened record writes its members the same way.
    #[test]
    fn a_flattened_record_writes_kind_first() {
        #[derive(Serialize)]
        struct Holder {
            first: i32,
            #[serde(flatten)]
            rest: FeatureRecord,
        }
        let rest: FeatureRecord = json!({"parameters": {"speed": 2, "kind": "wave"}})
            .as_object()
            .cloned()
            .unwrap()
            .into();
        assert_eq!(
            serde_json::to_string(&Holder { first: 1, rest }).unwrap(),
            r#"{"first":1,"parameters":{"kind":"wave","speed":2}}"#
        );
    }
}

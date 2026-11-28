//! JSON that managed binds with its polymorphic `LightingParameters`
//! (`[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]`, Lighting.cs).
//! Managed's serializer does not admit out-of-order metadata, so it refuses a
//! lighting record whose `kind` is not the first member of its object.
//!
//! serde_json keeps an object's members sorted, so a lighting record native
//! carries as a `Value` loses that order when written as is. `KindFirst`
//! writes `kind` first in every object the value holds, at any depth, arrays
//! included, and every other member in its sorted place. `kind` is the only
//! such discriminator in managed's contracts, and an ordinary member named
//! `kind` keeps its meaning wherever it sits.
use serde::{Serialize, Serializer, ser::SerializeMap};
use serde_json::{Map, Value};

/// Managed's polymorphic discriminator.
const KIND: &str = "kind";

/// `.0` written with `kind` first in each object it holds.
pub struct KindFirst<'a>(pub &'a Value);

impl Serialize for KindFirst<'_> {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        match self.0 {
            Value::Object(members) => object(members, serializer),
            Value::Array(items) => serializer.collect_seq(items.iter().map(KindFirst)),
            value => value.serialize(serializer),
        }
    }
}

/// `members` as one object, `kind` first, each value written by `KindFirst`.
pub fn object<S: Serializer>(
    members: &Map<std::string::String, Value>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    let mut map = serializer.serialize_map(Some(members.len()))?;
    if let Some(kind) = members.get(KIND) {
        map.serialize_entry(KIND, &KindFirst(kind))?;
    }
    for (name, value) in members.iter().filter(|(name, _)| *name != KIND) {
        map.serialize_entry(name, &KindFirst(value))?;
    }
    map.end()
}

/// `#[serde(serialize_with)]` for a member managed binds as lighting.
pub fn serialize<S: Serializer>(value: &Value, serializer: S) -> Result<S::Ok, S::Error> {
    KindFirst(value).serialize(serializer)
}

/// `#[serde(serialize_with)]` for an optional member managed binds as
/// lighting.
pub fn serialize_option<S: Serializer>(
    value: &Option<Value>,
    serializer: S,
) -> Result<S::Ok, S::Error> {
    match value {
        Some(value) => serializer.serialize_some(&KindFirst(value)),
        None => serializer.serialize_none(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::{format, string::ToString};

    /// `kind` comes first at every depth, inside arrays too, and the value
    /// reads back unchanged.
    #[test]
    fn kind_is_written_first_in_every_object() {
        let lit = json!({"color": {"blue": 5, "green": 10, "red": 200}, "kind": "static"});
        let value = json!({
            "lighting": {"brightness": 10, "parameters": lit},
            "rules": [{"parameters": lit}],
            "kind": 3,
        });
        let parameters = r#"{"kind":"static","color":{"blue":5,"green":10,"red":200}}"#;
        let written = serde_json::to_string(&KindFirst(&value)).unwrap();
        assert_eq!(
            written,
            format!(
                r#"{{"kind":3,"lighting":{{"brightness":10,"parameters":{parameters}}},"rules":[{{"parameters":{parameters}}}]}}"#
            )
        );
        assert_eq!(serde_json::from_str::<Value>(&written).unwrap(), value);
        // Scalars and null are written as they are.
        assert_eq!(
            serde_json::to_string(&KindFirst(&json!(null))).unwrap(),
            "null"
        );
        assert_eq!(
            serde_json::to_string(&KindFirst(&json!("kind"))).unwrap(),
            r#""kind""#.to_string()
        );
    }
}

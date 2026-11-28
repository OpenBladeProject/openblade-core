//! Helpers shared by the managed-vector tests: the vectors' edit format and
//! the production RZ09-0581 validator they are recorded against.
#![allow(dead_code)] // Each test target uses its own subset.

use openblade_domain::profile_set::json::Json;
use openblade_domain::profile_set::text::same_ordinal_ignoring_case;
use openblade_domain::profile_set::{Capabilities, ValidationPolicy, Validator};

pub fn member<'a>(value: &'a Json, name: &str) -> &'a Json {
    value.member(name).unwrap_or(&Json::Null)
}

fn step<'a>(parent: &'a mut Json, key: &Json) -> &'a mut Json {
    match (parent, key) {
        (Json::Object(members), Json::String(name)) => {
            &mut members
                .iter_mut()
                .find(|(member, _)| member == name)
                .expect(name)
                .1
        }
        (Json::Array(items), Json::Number(index)) => &mut items[index.parse::<usize>().unwrap()],
        other => panic!("unreachable path step {other:?}"),
    }
}

/// Each edit is `[path, value]` to set a member or `[path]` to remove it.
pub fn apply(base: &Json, edits: &Json) -> Json {
    let mut result = base.clone();
    let Json::Array(edits) = edits else {
        return result;
    };
    for edit in edits {
        let (Json::Array(pair), Some(Json::Array(path))) = (edit, edit_path(edit)) else {
            panic!("malformed edit");
        };
        let Some((Json::String(name), parents)) = path.split_last() else {
            panic!("an edit names a member");
        };
        let mut parent = &mut result;
        for key in parents {
            parent = step(parent, key);
        }
        let Json::Object(members) = parent else {
            panic!("an edit targets an object");
        };
        let at = members.iter().position(|(member, _)| member == name);
        match (pair.get(1), at) {
            (Some(value), Some(at)) => members[at].1 = value.clone(),
            (Some(value), None) => members.push((name.clone(), value.clone())),
            (None, Some(at)) => {
                members.remove(at);
            }
            (None, None) => {}
        }
    }
    result
}

fn edit_path(edit: &Json) -> Option<&Json> {
    match edit {
        Json::Array(pair) => pair.first(),
        _ => None,
    }
}

pub fn validator() -> Validator {
    let capabilities = Capabilities::rz09_0581();
    Validator::new(
        ValidationPolicy::for_capabilities(&capabilities, &capabilities),
        same_ordinal_ignoring_case,
    )
}

//! A bounded projection of Windows battery XML through the inbox XML reader.
#![allow(unsafe_code)]

use std::{
    ptr::{from_ref, null, null_mut},
    slice, str,
};
use windows_sys::Win32::Networking::WindowsWebServices::*;

pub(super) const MAX_BYTES: usize = 4 * 1024 * 1024;
pub(super) const MAX_USAGE: usize = 1024;

#[derive(Default)]
pub(super) struct Element {
    pub name: &'static str,
    pub text: String,
    pub attributes: Vec<(&'static str, String)>,
    pub children: Vec<Element>,
}

impl Element {
    pub fn child(&self, name: &str) -> Option<&Element> {
        self.children.iter().find(|child| child.name == name)
    }

    pub fn attr(&self, name: &str) -> Option<&str> {
        self.attributes
            .iter()
            .find(|attr| attr.0 == name)
            .map(|attr| attr.1.as_str())
    }
}

fn fields(parent: &str) -> &[&'static str] {
    match parent {
        "BatteryReport" => &[
            "ReportInformation",
            "RuntimeEstimates",
            "Batteries",
            "RecentUsage",
        ],
        "ReportInformation" => &["ScanTime", "ReportStartTime"],
        "RuntimeEstimates" => &["DesignCapacity", "FullChargeCapacity"],
        "Batteries" => &["Battery"],
        "Battery" => &["DesignCapacity", "FullChargeCapacity", "CycleCount"],
        "DesignCapacity" | "FullChargeCapacity" => &["Capacity"],
        "RecentUsage" => &["UsageEntry"],
        _ => &[],
    }
}

const USAGE_ATTRIBUTES: &[&str] = &[
    "Timestamp",
    "Duration",
    "Ac",
    "EntryType",
    "ChargeCapacity",
    "FullChargeCapacity",
    "Discharge",
];

struct Reader(*mut WS_XML_READER);

impl Drop for Reader {
    fn drop(&mut self) {
        // SAFETY: Created by WsCreateReader and uniquely owned here.
        unsafe { WsFreeReader(self.0) }
    }
}

pub(super) fn read(xml: &[u8]) -> Option<Element> {
    if xml.is_empty() || xml.len() > MAX_BYTES {
        return None;
    }
    let encoding = WS_XML_READER_TEXT_ENCODING {
        encoding: WS_XML_READER_ENCODING {
            encodingType: WS_XML_READER_ENCODING_TYPE_TEXT,
        },
        charSet: WS_CHARSET_AUTO,
    };
    let input = WS_XML_READER_BUFFER_INPUT {
        input: WS_XML_READER_INPUT {
            inputType: WS_XML_READER_INPUT_TYPE_BUFFER,
        },
        encodedData: xml.as_ptr().cast_mut().cast(),
        encodedDataSize: xml.len() as u32,
    };
    let mut raw = null_mut();
    // SAFETY: All pointers name live structures of the requested types.
    // The input buffer remains live until the reader is freed. The text
    // reader handles namespaces and character references, and rejects DTDs.
    let reader = unsafe {
        if WsCreateReader(null(), 0, &mut raw, null()) < 0 {
            return None;
        }
        let reader = Reader(raw);
        if WsSetInput(
            raw,
            from_ref(&encoding).cast(),
            from_ref(&input).cast(),
            null(),
            0,
            null(),
        ) < 0
        {
            return None;
        }
        reader
    };

    let mut stack: Vec<Option<Element>> = Vec::new();
    let mut root = None;
    loop {
        let mut node = null_mut();
        // SAFETY: The reader is valid. Each node and its borrowed values are
        // consumed or copied before advancing the reader.
        unsafe {
            if WsReadNode(reader.0, null()) < 0 || WsGetReaderNode(reader.0, &mut node, null()) < 0
            {
                return None;
            }
            match (*node).nodeType {
                WS_XML_NODE_TYPE_ELEMENT => {
                    let source = &*node.cast::<WS_XML_ELEMENT_NODE>();
                    let local = xml_string(&*source.localName)?;
                    let keep = match stack.last() {
                        None if root.is_none() && local == "BatteryReport" => Some("BatteryReport"),
                        None => return None,
                        Some(Some(parent)) => fields(parent.name).iter().copied().find(|&name| {
                            name == local
                                && (matches!(name, "UsageEntry" | "Battery")
                                    || parent.child(name).is_none())
                        }),
                        Some(None) => None,
                    };
                    let mut element = keep.map(|name| Element {
                        name,
                        ..Element::default()
                    });
                    if keep == Some("UsageEntry") {
                        let attrs = &mut element.as_mut()?.attributes;
                        for index in 0..source.attributeCount as usize {
                            let attr = &**source.attributes.add(index);
                            if attr.isXmlNs != 0 {
                                continue;
                            }
                            let local = xml_string(&*attr.localName)?;
                            if let Some(&name) =
                                USAGE_ATTRIBUTES.iter().find(|&&name| name == local)
                                && !attrs.iter().any(|&(existing, _)| existing == name)
                            {
                                attrs.push((name, xml_text(attr.value)?.to_owned()));
                            }
                        }
                    }
                    // The reader emits an end-element event for empty tags too.
                    stack.push(element);
                }
                WS_XML_NODE_TYPE_END_ELEMENT => attach(stack.pop()?, &mut stack, &mut root)?,
                WS_XML_NODE_TYPE_TEXT => {
                    // XDocument's Value includes text inside nested elements.
                    if let Some(element) = stack.iter_mut().rev().flatten().next()
                        && matches!(
                            element.name,
                            "ScanTime"
                                | "ReportStartTime"
                                | "DesignCapacity"
                                | "FullChargeCapacity"
                                | "Capacity"
                                | "CycleCount"
                        )
                    {
                        let source = &*node.cast::<WS_XML_TEXT_NODE>();
                        element.text.push_str(xml_text(source.text)?);
                    }
                }
                WS_XML_NODE_TYPE_EOF => return if stack.is_empty() { root } else { None },
                _ => {}
            }
        }
    }
}

fn attach(
    element: Option<Element>,
    stack: &mut [Option<Element>],
    root: &mut Option<Element>,
) -> Option<()> {
    if let Some(element) = element {
        match stack.last_mut() {
            None => *root = Some(element),
            Some(Some(parent)) => match element.name {
                "UsageEntry" if parent.children.len() == MAX_USAGE => return None,
                "Battery" if parent.children.len() == 17 => {}
                "Battery" | "UsageEntry" => parent.children.push(element),
                name if parent.child(name).is_none() => parent.children.push(element),
                _ => {}
            },
            Some(None) => unreachable!("only recognized paths are projected"),
        }
    }
    Some(())
}

// SAFETY for both accessors: the value and the reader-owned buffer must be
// live, and the buffer must remain unchanged for the duration of the borrow.
unsafe fn xml_string(value: &WS_XML_STRING) -> Option<&str> {
    if value.length == 0 {
        return Some("");
    }
    str::from_utf8(unsafe { slice::from_raw_parts(value.bytes, value.length as usize) }).ok()
}

unsafe fn xml_text<'a>(value: *const WS_XML_TEXT) -> Option<&'a str> {
    if unsafe { (*value).textType } != WS_XML_TEXT_TYPE_UTF8 {
        return None;
    }
    let utf8 = unsafe { &*(value.cast::<WS_XML_UTF8_TEXT>()) };
    unsafe { xml_string(&utf8.value) }
}

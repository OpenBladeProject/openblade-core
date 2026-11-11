//! Scratch-only noninheritable channels; launch arguments carry routing, no secret.
#![allow(unsafe_code)]
use std::{
    fs::{File, OpenOptions},
    io,
    os::windows::io::AsRawHandle,
};
use windows_sys::Win32::System::Pipes::GetNamedPipeServerProcessId;

pub(super) fn parse(args: &[String]) -> io::Result<(&str, u32)> {
    if args.len() != 3
        || args[0] != "--scratch-channels"
        || args[1].len() != 32
        || !args[1]
            .bytes()
            .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
        || args[1].bytes().all(|c| c == b'0')
        || args[2].is_empty()
        || !args[2].bytes().all(|c| c.is_ascii_digit())
    {
        return Err(io::Error::other("invalid scratch channel arguments"));
    }
    let parent = args[2].parse::<u32>().map_err(io::Error::other)?;
    if parent == 0 {
        return Err(io::Error::other("invalid scratch launcher PID"));
    }
    Ok((&args[1], parent))
}

pub(super) fn open(args: &[String]) -> io::Result<[File; 3]> {
    let (id, parent) = parse(args)?;
    let mut files = Vec::new();
    for index in 0..3 {
        // std opens private noninheritable handles; no raw handle transfer.
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(format!(r"\\.\pipe\OpenBlade.Scratch.Channels.{id}.{index}"))?;
        let mut actual = 0;
        // SAFETY: file owns a live pipe endpoint and actual is a writable DWORD.
        if unsafe { GetNamedPipeServerProcessId(file.as_raw_handle(), &mut actual) } == 0
            || actual != parent
        {
            return Err(io::Error::other(
                "scratch channel server differs from its launcher",
            ));
        }
        files.push(file);
    }
    Ok(files.try_into().expect("three fixed channels"))
}

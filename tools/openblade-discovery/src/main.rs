#[cfg(any(target_os = "linux", test))]
mod linux;
#[cfg(target_os = "macos")]
#[allow(unsafe_code)]
mod macos;
#[cfg(any(target_os = "linux", target_os = "macos", test))]
mod snapshot;

use std::process::ExitCode;

fn main() -> ExitCode {
    if std::env::args_os().nth(1).is_some() {
        eprintln!("usage: openblade-discovery (no arguments)");
        return ExitCode::from(2);
    }
    run()
}

#[cfg(any(target_os = "linux", target_os = "macos"))]
fn run() -> ExitCode {
    use std::io::{self, Write};

    #[cfg(target_os = "linux")]
    let snapshot = linux::discover(std::path::Path::new("/sys"));
    #[cfg(target_os = "macos")]
    let snapshot = macos::discover();

    let mut stdout = io::stdout().lock();
    if serde_json::to_writer(&mut stdout, &snapshot).is_err()
        || stdout
            .write_all(b"\n")
            .and_then(|()| stdout.flush())
            .is_err()
    {
        eprintln!("OpenBlade discovery: output failed");
        return ExitCode::FAILURE;
    }
    ExitCode::SUCCESS
}

#[cfg(not(any(target_os = "linux", target_os = "macos")))]
fn run() -> ExitCode {
    eprintln!("OpenBlade discovery: unsupported platform (Linux or macOS required)");
    ExitCode::FAILURE
}

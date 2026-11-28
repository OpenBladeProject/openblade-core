#[cfg(windows)]
mod session_audio;
#[cfg(windows)]
mod session_battery;
#[cfg(windows)]
mod session_brightness;
#[cfg(windows)]
mod session_display;
#[cfg(windows)]
mod session_gpu;
#[cfg(windows)]
mod session_windows;

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    let result = session_windows::entry();
    #[cfg(not(windows))]
    let result: std::io::Result<()> = Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "the development Session Agent requires Windows",
    ));

    match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("OpenBlade development Session Agent: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}

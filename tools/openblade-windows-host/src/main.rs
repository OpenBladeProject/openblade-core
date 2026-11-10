#[cfg(any(windows, test))]
mod battery_policy;
#[cfg(any(windows, test))]
mod battery_runtime;
#[cfg(any(windows, test))]
mod runtime;
#[cfg(windows)]
mod windows;
#[cfg(any(windows, test))]
mod wire;

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    let result = windows::entry();
    #[cfg(not(windows))]
    let result: std::io::Result<()> = Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "the development host requires Windows",
    ));

    match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("OpenBlade development host: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}

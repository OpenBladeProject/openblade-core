#[cfg(any(windows, test))]
mod discovery;
#[cfg(any(windows, test))]
mod registry_identity;

fn main() -> std::process::ExitCode {
    #[cfg(windows)]
    let result = discovery::windows::run();
    #[cfg(not(windows))]
    let result: std::io::Result<()> = Err(std::io::Error::new(
        std::io::ErrorKind::Unsupported,
        "the development discovery tool requires Windows",
    ));

    match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("OpenBlade Windows discovery: {error}");
            std::process::ExitCode::FAILURE
        }
    }
}

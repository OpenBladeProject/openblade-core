mod transport;
#[cfg_attr(not(windows), allow(dead_code))]
mod wire;

fn main() -> std::process::ExitCode {
    if std::env::args_os().len() != 1 {
        eprintln!("usage: openblade-status-cli (one read-only GetStatus; no arguments)");
        return std::process::ExitCode::from(2);
    }
    match transport::get_status() {
        Ok(summary) => {
            println!("{}", summary);
            std::process::ExitCode::SUCCESS
        }
        Err(error) => {
            // Errors are fixed categories; never print remote messages, JSON or paths.
            eprintln!("OpenBlade status: {error}");
            std::process::ExitCode::from(1)
        }
    }
}

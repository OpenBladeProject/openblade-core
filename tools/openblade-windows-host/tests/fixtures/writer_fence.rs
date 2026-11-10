#[cfg(windows)]
fn main() -> std::process::ExitCode {
    use std::io::{self, BufRead, Write};
    use std::process::ExitCode;

    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let [path, root, lock, generation] = args.as_slice() else {
        return ExitCode::from(3);
    };
    let (Some(root), Some(lock), Some(generation)) =
        (root.to_str(), lock.to_str(), generation.to_str())
    else {
        return ExitCode::from(3);
    };
    if generation.len() != 32 || !generation.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return ExitCode::from(3);
    }
    let lease = match openblade_windows_host::configuration_writer::scratch::acquire(
        path.as_ref(),
        root,
        lock,
    ) {
        Ok(lease) => lease,
        Err(error) if error.raw_os_error() == Some(32) => return ExitCode::from(2),
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::from(3);
        }
    };
    let mut commands = io::stdin().lock().lines();
    println!("held");
    if io::stdout().flush().is_err()
        || !matches!(commands.next(), Some(Ok(command)) if command == "retire")
    {
        return ExitCode::from(3);
    }
    drop(lease);
    println!("retired:candidate:{generation}");
    if io::stdout().flush().is_err()
        || !matches!(commands.next(), Some(Ok(command)) if command == "exit")
    {
        return ExitCode::from(3);
    }
    ExitCode::SUCCESS
}

#[cfg(not(windows))]
fn main() -> std::process::ExitCode {
    std::process::ExitCode::FAILURE
}

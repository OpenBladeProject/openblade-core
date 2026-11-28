#[cfg(windows)]
fn main() -> std::process::ExitCode {
    use openblade_windows_host::configuration_writer::scratch::{
        acquire,
        run_marker::{RunMarkerStore, StartupKind},
    };
    use std::io::{self, BufRead, Write};
    use std::process::ExitCode;

    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let (path, root, lock, generation, begin_run_marker) = match args.as_slice() {
        [path, root, lock, generation] => (path, root, lock, generation, false),
        [mode, path, root, lock, generation]
            if mode == "--phase-8o-run-marker"
                && std::env::var("OPENBLADE_PHASE_8K_RETIREMENT").as_deref() == Ok("1") =>
        {
            (path, root, lock, generation, true)
        }
        _ => return ExitCode::from(3),
    };
    let (Some(root), Some(lock), Some(generation)) =
        (root.to_str(), lock.to_str(), generation.to_str())
    else {
        return ExitCode::from(3);
    };
    if generation.len() != 32 || !generation.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return ExitCode::from(3);
    }
    let startup = if begin_run_marker {
        let mut store = RunMarkerStore::new(path.as_ref(), root, lock);
        // Leave the confirmed marker for the managed reader. The lease
        // retirement protocol below has no run-marker cleanup authority.
        match store.begin_run() {
            Ok(kind) => Some(kind),
            Err(error) => {
                eprintln!("{error}");
                return ExitCode::from(3);
            }
        }
    } else {
        None
    };
    let lease = match acquire(path.as_ref(), root, lock) {
        Ok(lease) => lease,
        Err(error) if error.raw_os_error() == Some(32) => return ExitCode::from(2),
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::from(3);
        }
    };
    let mut commands = io::stdin().lock().lines();
    println!("held");
    if let Some(kind) = startup {
        let classification = match kind {
            StartupKind::Normal => "Normal",
            StartupKind::UnexpectedExitRecovery => "UnexpectedExitRecovery",
        };
        println!("marker:{classification}");
    }
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

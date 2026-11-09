use std::io::{self, BufRead, Write};

use openblade_windows_host::pipe::{Endpoint, stale_connection};
use tokio::{runtime::Builder, sync::oneshot};

use crate::session_display::{self, DisplayFixture, Rejected};

const PREFIX: &str = r"\\.\pipe\OpenBlade.Rust.Session.Display.Dev.";

pub(super) fn entry() -> io::Result<()> {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let [mode, id, scenario] = args.as_slice() else {
        return Err(usage());
    };
    let id = id.to_str().filter(|id| valid_id(id)).ok_or_else(usage)?;
    if mode != "foreground" {
        return Err(usage());
    }
    let fixture = DisplayFixture::load(scenario.to_str().ok_or_else(usage)?)?;
    let runtime = Builder::new_current_thread().enable_all().build()?;
    runtime.block_on(run(id, fixture))
}

fn usage() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidInput,
        "expected foreground <32 lowercase hex digits> <available|unavailable>",
    )
}

fn valid_id(id: &str) -> bool {
    id.len() == 32
        && id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

async fn run(id: &str, fixture: DisplayFixture) -> io::Result<()> {
    // One reserved claim and one data connection. This endpoint has no
    // subscribers or concurrent dispatch, so partial frames can occupy one slot.
    let endpoint = Endpoint::new(format!("{PREFIX}{id}"), 2)?;
    let _claim = endpoint.claim()?;
    let listener = endpoint.listen()?;

    let (stop, stopped) = oneshot::channel();
    std::thread::Builder::new()
        .name("session-development-stop".into())
        .spawn(move || {
            let _ = io::stdin().lock().fill_buf();
            let _ = stop.send(());
        })?;
    {
        let mut output = io::stdout().lock();
        writeln!(output, "READY")?;
        output.flush()?;
    }

    let serve = async {
        let mut listener = listener;
        loop {
            match listener.connect().await {
                Ok(()) => {
                    if let Err(Rejected::InvalidFixture) =
                        session_display::serve(&mut listener, &endpoint.identity, &fixture).await
                    {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "embedded Session Display response cannot be framed",
                        ));
                    }
                }
                Err(error) if stale_connection(&error) => {}
                Err(error) => return Err(error),
            }
            // Drop the old stream, including any buffered bytes. Keep the
            // synchronous claim until shutdown so the name never goes unowned.
            drop(listener);
            listener = endpoint.next_listener().await?;
        }
    };
    tokio::select! {
        biased;
        _ = stopped => Ok(()),
        result = serve => result,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_only_fixed_width_lowercase_hex_for_private_names() {
        assert!(valid_id("11111111222243338444555555555555"));
        for invalid in [
            "OpenBlade.Session.Display.v1",
            r"\\.\pipe\OpenBlade.Session.Display.v1",
            "1111111122224333844455555555555A",
            "1111111122224333844455555555555",
        ] {
            assert!(!valid_id(invalid));
        }
    }
}

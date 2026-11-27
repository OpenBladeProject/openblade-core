//! The session power relay's use of the shared report exchange.
use std::cell::Cell;

use super::*;

/// Counts how often the package is re-read to establish a generation.
struct Counting {
    inner: Authority,
    acquired: Cell<u32>,
}

impl InstalledServiceAuthority for Counting {
    fn acquire_generation(
        &self,
    ) -> Result<Box<dyn InstalledServiceGeneration + '_>, ServiceAuthorityError> {
        self.acquired.set(self.acquired.get() + 1);
        self.inner.acquire_generation()
    }
}

/// One exchange against a server that answers each request with `reply`
/// and returns what it read, if anything.
async fn exchange_once(
    channel: &mut ReportChannel<'_>,
    peer: impl Fn(&DuplexStream) -> io::Result<(u32, u32)>,
    reply: &[u8],
) -> (io::Result<Vec<u8>>, Option<Vec<u8>>) {
    let (client, mut server) = duplex(16_384);
    let exchange = channel.exchange_on(
        b"{\"request\":1}",
        Duration::from_secs(2),
        async || Ok(client),
        peer,
    );
    let serving = async move {
        let read = read_payload(&mut server).await.ok()?;
        let frame = [(reply.len() as i32).to_le_bytes().as_slice(), reply].concat();
        server.write_all(&frame).await.unwrap();
        Some(read)
    };
    tokio::join!(exchange, serving)
}

#[tokio::test(start_paused = true)]
async fn a_primed_channel_exchanges_without_rereading_the_package() {
    let authority = Counting {
        inner: Authority::live(),
        acquired: Cell::new(0),
    };
    let mut channel = ReportChannel::new(Some(&authority));
    channel.prime().unwrap();
    for _ in 0..3 {
        let (reply, read) = exchange_once(&mut channel, |_| Ok((PID, 0)), b"{\"reply\":1}").await;
        assert_eq!(reply.unwrap(), b"{\"reply\":1}");
        assert_eq!(read.unwrap(), b"{\"request\":1}");
    }
    assert_eq!(authority.acquired.get(), 1);
}

#[tokio::test(start_paused = true)]
async fn nothing_is_exchanged_with_an_unverified_peer() {
    let authority = Counting {
        inner: Authority::live(),
        acquired: Cell::new(0),
    };
    let mut channel = ReportChannel::new(Some(&authority));
    channel.prime().unwrap();
    let (reply, read) = exchange_once(&mut channel, |_| Ok((PID + 1, 0)), b"{}").await;
    assert_eq!(reply.unwrap_err().kind(), io::ErrorKind::PermissionDenied);
    assert!(read.is_none());
    // The failed verification dropped the generation; the next exchange
    // acquires a new one.
    let (reply, _) = exchange_once(&mut channel, |_| Ok((PID, 0)), b"{}").await;
    reply.unwrap();
    assert_eq!(authority.acquired.get(), 2);
}

#[tokio::test(start_paused = true)]
async fn priming_without_an_authority_fails() {
    let mut channel = ReportChannel::new(None);
    assert_eq!(
        channel.prime().unwrap_err().kind(),
        io::ErrorKind::Unsupported
    );
}

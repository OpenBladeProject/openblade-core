use tokio::sync::oneshot;

use super::*;
use crate::discovery::hid_lease::owner::Epoch;

pub(crate) struct Lease {
    source: Rc<Fake>,
    owner: HidLeaseOwner<Rc<Fake>>,
    epoch: Option<Epoch>,
    cancelled: AtomicBool,
}

pub(crate) struct Script(Rc<Fake>);

impl Script {
    pub(crate) fn on_open(&self, on_open: impl FnOnce() + 'static) {
        assert!(self.0.on_open.replace(Some(Box::new(on_open))).is_none());
    }

    pub(crate) fn defer_retirement(&self) {
        self.0.retirements.borrow_mut().push_back(Poll::Pending);
    }
}

impl Lease {
    pub(crate) fn new() -> (Self, Script) {
        let source = fake();
        let owner = scripted_owner(&source);
        let script = Script(Rc::clone(&source));
        (
            Self {
                owner,
                source,
                epoch: None,
                cancelled: AtomicBool::new(false),
            },
            script,
        )
    }

    pub(crate) fn invalidate(&mut self) {
        if let Some(epoch) = self.epoch.take() {
            assert!(self.owner.invalidate(&epoch).is_some());
            assert!(self.owner.revalidate(&epoch, &self.cancelled).is_err());
        }
    }

    pub(crate) fn refresh(&mut self) -> io::Result<bool> {
        match self.owner.poll_retirement() {
            Retirement::Pending => return Ok(false),
            Retirement::Uncertain => return Err(io::Error::other("HID retirement is uncertain")),
            Retirement::Complete => {}
        }
        let verified = if let Some(epoch) = &self.epoch {
            if self.owner.revalidate(epoch, &self.cancelled).is_ok() {
                true
            } else {
                self.epoch = None;
                false
            }
        } else {
            self.epoch = self.owner.acquire(&self.cancelled).ok();
            self.epoch.is_some()
        };
        if self.owner.retirement() == Retirement::Uncertain {
            return Err(io::Error::other("HID retirement is uncertain"));
        }
        Ok(verified)
    }

    pub(crate) fn counts(&self) -> (usize, usize) {
        (self.source.opened.borrow().len(), self.source.drops.get())
    }

    pub(crate) async fn stop(
        &mut self,
        draining: oneshot::Sender<()>,
        release: oneshot::Receiver<()>,
    ) -> io::Result<()> {
        match self.owner.stop() {
            Retirement::Complete => {}
            Retirement::Uncertain => return Err(io::Error::other("HID retirement is uncertain")),
            Retirement::Pending => {
                draining
                    .send(())
                    .map_err(|()| io::Error::other("HID drain observer disconnected"))?;
                release.await.map_err(io::Error::other)?;
                if self.owner.poll_retirement() != Retirement::Complete {
                    return Err(io::Error::other("HID retirement did not settle"));
                }
            }
        }
        assert!(self.owner.acquire(&self.cancelled).is_err());
        assert_eq!(self.counts().0, self.counts().1);
        Ok(())
    }
}

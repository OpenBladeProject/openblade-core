//! Battery request overrides follow managed process/user and source lifetimes.
//! Protected attempt rows retain requested/configured intent without converting
//! a temporary override into restart-persistent profiles or manual overrides.
use super::*;
use openblade_domain::battery_history::Desired;
/// What an automatic source cancel's claim took from storage, its intent,
/// and the reservation it left in place.
#[cfg(feature = "service-temporary-charge-v1")]
pub(in super::super) struct TemporaryTaken {
    intent: Option<bool>,
    capacity: usize,
    origin: Option<openblade_domain::temporary_charge_history::Origin>,
}
#[derive(Debug)]
pub(in super::super) enum TemporaryRelease {
    Released,
    NoMatchingReservation,
    Deferred(ReleaseDeferred),
}
/// The journal space `entry`'s enable reserves for its automatic cancel: the
/// cancel's four rows, or its Prepared and known-not-submitted rows followed
/// by a fresh-ID retry's four (`retry_capacity`), whichever is larger.
fn cancellation_capacity(entry: &Entry) -> io::Result<usize> {
    let Some(row) = cancellation_row(entry)? else {
        return Ok(0);
    };
    let row = row_len(&row)?;
    Ok((row * 4).max(row * 2 + retry_capacity(entry)?))
}
/// The four rows of a fresh-ID retry of `entry`'s automatic cancel, at the
/// size of its largest: three-digit fresh IDs, and the reserved IDs named.
fn retry_capacity(entry: &Entry) -> io::Result<usize> {
    let Some(mut row) = cancellation_row(entry)? else {
        return Ok(0);
    };
    let cancel = row.tfc.as_mut().ok_or_else(denied)?;
    cancel.superseded_ids = Some(cancel.ids);
    cancel.ids = [254; openblade_domain::temporary_charge_history::EXCHANGES];
    Ok(row_len(&row)? * 4)
}
fn row_len(row: &Entry) -> io::Result<usize> {
    Ok(serde_json::to_vec(row)?.len() + 1)
}
/// The largest automatic cancel row the journal admits for `entry`'s enable,
/// with its reserved IDs. `None` for a row that reserves no cancellation.
fn cancellation_row(entry: &Entry) -> io::Result<Option<Entry>> {
    let Some(t) = entry.tfc.as_ref().filter(|t| t.cancel_ids.is_some()) else {
        return Ok(None);
    };
    let mut cancel = t.clone();
    cancel.requested =
        openblade_domain::battery_history::Request::SetFullChargeOverride { enabled: false };
    cancel.configured.temporary = false;
    cancel.desired.temporary = false;
    // Bound future source-profile booleans and a newly acquired fan binding.
    cancel.configured.protection = false;
    cancel.desired.protection = false;
    cancel.fan_owner = Some("0".repeat(64));
    cancel.source = 1;
    cancel.baseline = 4;
    cancel.target = 0;
    cancel.policy = None;
    cancel.ids = t.cancel_ids.ok_or_else(denied)?;
    cancel.cancel_ids = None;
    cancel.origin = Some(openblade_domain::temporary_charge_history::Origin {
        request: entry.request.clone(),
        intent: t.hash()?,
    });
    let mut row = entry.clone();
    row.tfc = Some(cancel);
    row.progress = 72;
    Ok(Some(row))
}
impl Storage {
    #[cfg(all(test, feature = "service-temporary-charge-v1"))]
    pub(in super::super) fn temporary_capacity_for_tests(
        &self,
    ) -> (
        usize,
        Option<openblade_domain::temporary_charge_history::Origin>,
    ) {
        let s = self.state.lock().unwrap();
        (s.temporary_capacity, s.temporary_capacity_origin.clone())
    }
    pub(in super::super) fn release_temporary_capacity(
        &self,
        origin: &openblade_domain::temporary_charge_history::Origin,
    ) -> io::Result<TemporaryRelease> {
        let mut s = self.state.lock().map_err(|_| {
            super::super::thermal_mutation::fatal_admission("Thermal storage state is poisoned.")
        })?;
        if s.temporary_capacity_origin.as_ref() != Some(origin) {
            return Ok(TemporaryRelease::NoMatchingReservation);
        }
        if s.quarantined {
            return Ok(TemporaryRelease::Deferred(ReleaseDeferred::reconciliation(
                pending_reason_locked(&s),
            )));
        }
        if s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
            return Ok(TemporaryRelease::Deferred(ReleaseDeferred::busy()));
        }
        let read = |name| match self.files.read_release(name) {
            Ok(bytes) => Ok(Ok(bytes)),
            Err(e) if is_release_read_unavailable(&e) => Ok(Err(ReleaseDeferred::unreadable(e))),
            Err(e) => Err(e),
        };
        match read("thermal-mutation.journal.pending")? {
            Ok(None) => {}
            Ok(Some(_)) => return Err(denied()),
            Err(reason) => return Ok(TemporaryRelease::Deferred(reason)),
        }
        let journal = match read(JOURNAL)? {
            Ok(bytes) => bytes,
            Err(reason) => return Ok(TemporaryRelease::Deferred(reason)),
        };
        if journal != s.journal {
            return Err(denied());
        }
        // Bookkeeping only: no history/IDs/fan reservation is rewritten.
        s.temporary_capacity = 0;
        s.temporary_capacity_origin = None;
        s.temporary_retry_capacity = 0;
        Ok(TemporaryRelease::Released)
    }
    #[cfg(all(test, feature = "service-temporary-charge-v1"))]
    pub(in super::super) fn temporary_desired(&self) -> bool {
        self.state
            .lock()
            .is_ok_and(|s| s.battery_temporary == Some(true))
    }
    pub(in super::super) fn clear_battery_temporary(&self) {
        if let Ok(mut state) = self.state.lock() {
            state.battery_temporary = None;
        }
    }
    /// Clears the intent at an automatic source cancel's claim, and records
    /// it with the cancellation reservation the claim leaves in place, for
    /// `restore_temporary_claim`. `None`, with nothing cleared, when the
    /// state is poisoned.
    #[cfg(feature = "service-temporary-charge-v1")]
    pub(in super::super) fn take_temporary_claim(&self) -> Option<TemporaryTaken> {
        let mut s = self.state.lock().ok()?;
        Some(TemporaryTaken {
            intent: s.battery_temporary.take(),
            capacity: s.temporary_capacity,
            origin: s.temporary_capacity_origin.clone(),
        })
    }
    /// Puts back exactly what `take_temporary_claim` took, or nothing. The
    /// intent goes back only into an empty slot, never over a newer one, and
    /// only while the reservation is still the one the claim held: a row that
    /// spent or replaced it means the attempt got further than its claim.
    #[cfg(feature = "service-temporary-charge-v1")]
    pub(in super::super) fn restore_temporary_claim(&self, taken: TemporaryTaken) -> bool {
        let Ok(mut s) = self.state.lock() else {
            return false;
        };
        if s.quarantined
            || s.last.as_ref().is_some_and(|e| !settled(e.progress))
            || s.battery_temporary.is_some()
        {
            return false;
        }
        if s.temporary_capacity != taken.capacity || s.temporary_capacity_origin != taken.origin {
            return false;
        }
        s.battery_temporary = taken.intent;
        true
    }
    /// Puts back the intent a claim took, for a retry with fresh IDs after
    /// `attempt` sent some of the reserved ones and submitted no setter, and
    /// reserves the retry's own rows (`retry_capacity`). Like
    /// `restore_temporary_claim`, it refuses over quarantined or unsettled
    /// storage or a newer intent, and unless the attempt left the reservation
    /// as the claim saw it, or journaled only Prepared and known not
    /// submitted, whose Prepared spent it. The room the retry reserves is what
    /// that Prepared checked it left (`record_battery`), and nothing else
    /// writes while the claim is decided.
    #[cfg(feature = "service-temporary-charge-v1")]
    pub(in super::super) fn restore_spent_claim(
        &self,
        taken: TemporaryTaken,
        attempt: &str,
    ) -> bool {
        let Ok(mut s) = self.state.lock() else {
            return false;
        };
        if s.quarantined
            || s.last.as_ref().is_some_and(|e| !settled(e.progress))
            || s.battery_temporary.is_some()
            || taken.origin.is_none()
            || s.temporary_retry_capacity == 0
        {
            return false;
        }
        let left = match s.last.as_ref().filter(|e| e.request == attempt) {
            Some(last) => {
                last.progress == 6
                    && s.temporary_capacity == 0
                    && s.temporary_capacity_origin.is_none()
            }
            None => {
                s.temporary_capacity == taken.capacity
                    && s.temporary_capacity_origin == taken.origin
            }
        };
        let room = s
            .journal
            .as_deref()
            .unwrap_or_default()
            .len()
            .checked_add(s.temporary_retry_capacity)
            .is_some_and(|n| n <= crate::thermal_history::SEGMENT);
        if !left || !room {
            return false;
        }
        s.temporary_capacity = s.temporary_retry_capacity;
        s.temporary_capacity_origin = taken.origin;
        s.battery_temporary = taken.intent;
        true
    }
    pub(in super::super) fn desired_battery(&self, source: u8) -> io::Result<Desired> {
        let doc = aggregate(self.files.as_ref(), self.same_path)?;
        let profiles = doc.as_ref().map(|v| &v["profiles"]);
        let key = match source {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            _ => return Err(denied()),
        };
        let configured = if let Some(p) = profiles {
            let limit = p["batteryPolicy"]["chargeLimitPercent"]
                .as_u64()
                .filter(|v| (50..=99).contains(v))
                .ok_or_else(denied)? as u8;
            let protection = p[key]["batteryProtectionEnabled"].as_bool().unwrap_or(true);
            Desired {
                limit,
                protection,
                temporary: false,
            }
        } else {
            Desired {
                limit: 80,
                protection: true,
                temporary: false,
            }
        };
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.battery_protection.is_some_and(|(_, old)| old != source) {
            s.battery_protection = None;
        }
        if source == 1 {
            s.battery_temporary = None;
        }
        Ok(Desired {
            limit: s.battery_limit.unwrap_or(configured.limit),
            protection: s
                .battery_protection
                .map_or(configured.protection, |(v, _)| v),
            temporary: s.battery_temporary.unwrap_or(false),
        })
    }
    pub(in super::super) fn commit_battery(&self, source: u8, desired: Desired) -> io::Result<()> {
        let doc = aggregate(self.files.as_ref(), self.same_path)?;
        let key = match source {
            0 => "pluggedIn",
            1 => "battery",
            2 => "usbC",
            _ => return Err(denied()),
        };
        let baseline = doc.as_ref().map(|v| &v["profiles"]);
        let limit = baseline
            .and_then(|p| p["batteryPolicy"]["chargeLimitPercent"].as_u64())
            .unwrap_or(80) as u8;
        let protection = baseline
            .and_then(|p| p[key]["batteryProtectionEnabled"].as_bool())
            .unwrap_or(true);
        let mut s = self.state.lock().map_err(|_| denied())?;
        s.battery_limit = (desired.limit != limit).then_some(desired.limit);
        s.battery_protection =
            (desired.protection != protection).then_some((desired.protection, source));
        s.battery_temporary = desired.temporary.then_some(true);
        Ok(())
    }
    pub(in super::super) fn retained_fan_binding(&self) -> io::Result<Option<String>> {
        let s = self.state.lock().map_err(|_| denied())?;
        let mut held = None;
        crate::thermal_history::rows(s.journal.as_deref().unwrap_or_default(), |e, _| {
            if e.fan.is_some() {
                if e.progress == 30 {
                    held = Some(e.clone())
                } else if matches!(e.progress, 50 | 51) {
                    held = None
                }
            }
            Ok(())
        })?;
        held.as_ref()
            .map(crate::thermal_history::battery_fan_binding)
            .transpose()
    }
    pub(in super::super) fn record_battery(&self, mut entry: Entry) -> io::Result<()> {
        entry.user_sid = self.user_sid.clone();
        #[cfg(test)]
        if entry.battery.is_some()
            && entry.progress == 0
            && let Some(hook) = self.battery_append_hook.lock().unwrap().clone()
        {
            hook("before-prepared");
        }
        let mut s = self.state.lock().map_err(|_| denied())?;
        if s.quarantined {
            return Err(denied());
        }
        match (&entry.battery, &entry.tfc) {
            (Some(r), None) => r.validate()?,
            (None, Some(r)) => r.validate()?,
            _ => return Err(denied()),
        }
        if entry.progress == 6
            && (s.last.as_ref().is_none_or(|e| e.progress != 0)
                || self
                    .files
                    .read("thermal-mutation.journal.pending")?
                    .is_some()
                || self.files.read(JOURNAL)? != s.journal)
        {
            return Err(denied());
        }
        if entry.progress == 0 {
            if s.last.as_ref().is_some_and(|e| !settled(e.progress)) {
                return Err(denied());
            }
            let mut largest = entry.clone();
            largest.progress = if largest.tfc.is_some() { 72 } else { 62 };
            let row = serde_json::to_vec(&largest)?.len() + 1;
            // Preserve exact original fan handback rows as well as its transaction IDs.
            let mut retained = 0usize;
            crate::thermal_history::rows(s.journal.as_deref().unwrap_or_default(), |e, _| {
                if e.fan.is_some() {
                    if e.progress == 30 {
                        let mut v = e.clone();
                        v.progress = 50;
                        retained = (serde_json::to_vec(&v)?.len() + 1) * 6;
                    } else if matches!(e.progress, 50 | 51) {
                        retained = 0;
                    }
                }
                Ok(())
            })?;
            let own = match entry.tfc.as_ref().filter(|t| t.origin.is_some()) {
                // An automatic cancel with its enable's reserved IDs also leaves
                // room for a fresh-ID retry after its own Prepared and
                // known-not-submitted rows (`restore_spent_claim`).
                Some(t)
                    if t.superseded_ids.is_none()
                        && s.temporary_capacity_origin.as_ref() == t.origin.as_ref() =>
                {
                    (row * 4).max(row * 2 + s.temporary_retry_capacity)
                }
                Some(_) => row * 4,
                None => row * 4 + cancellation_capacity(&entry)? + s.temporary_capacity,
            };
            let needed = own + retained;
            if s.temporary_capacity > 0
                && s.journal
                    .as_deref()
                    .unwrap_or_default()
                    .len()
                    .checked_add(needed)
                    .is_none_or(|n| n > crate::thermal_history::SEGMENT)
            {
                return Err(crate::thermal_history::capacity_refusal());
            }
            match self.files.reserve(s.journal.as_deref(), needed) {
                Ok(v) => s.journal = v,
                Err(e) if crate::thermal_history::known_capacity_refusal(&e) => return Err(e),
                Err(e) => {
                    s.quarantined = true;
                    s.preparation_failed = true;
                    return Err(e);
                }
            }
        }
        let mut bytes = s.journal.clone().unwrap_or_default();
        bytes.extend(serde_json::to_vec(&entry)?);
        bytes.push(b'\n');
        crate::thermal_history::rows(&bytes, |_, _| Ok(()))?;
        let result = if entry.progress == 6 {
            self.files
                .replace_retired(JOURNAL, s.journal.as_deref(), &bytes)
        } else {
            self.files.replace(JOURNAL, s.journal.as_deref(), &bytes)
        };
        if let Err(e) = result {
            s.quarantined = true;
            s.preparation_failed = entry.progress == 0;
            s.intent_append_failed = matches!(entry.progress, 60 | 70);
            return Err(e);
        }
        s.journal = Some(bytes);
        if entry.progress == 0
            && entry.tfc.as_ref().is_some_and(|t| t.origin.is_some())
            && s.temporary_capacity_origin.as_ref()
                == entry.tfc.as_ref().and_then(|t| t.origin.as_ref())
        {
            s.temporary_capacity = 0;
            s.temporary_capacity_origin = None;
            // A fresh-ID retry is the last attempt; a reserved cancel keeps
            // what its retry would need.
            if entry
                .tfc
                .as_ref()
                .is_some_and(|t| t.superseded_ids.is_some())
            {
                s.temporary_retry_capacity = 0;
            }
        }
        if matches!(entry.progress, 72 | 73) {
            s.temporary_capacity = cancellation_capacity(&entry)?;
            s.temporary_capacity_origin = entry
                .tfc
                .as_ref()
                .filter(|t| t.desired.temporary)
                .map(|t| {
                    Ok::<_, io::Error>(openblade_domain::temporary_charge_history::Origin {
                        request: entry.request.clone(),
                        intent: t.hash()?,
                    })
                })
                .transpose()?;
            s.temporary_retry_capacity = if s.temporary_capacity_origin.is_some() {
                retry_capacity(&entry)?
            } else {
                0
            };
        }
        #[cfg(test)]
        let policy_prepared = entry.battery.is_some() && entry.progress == 0;
        s.last = Some(entry);
        #[cfg(test)]
        {
            drop(s);
            if policy_prepared && let Some(hook) = self.battery_append_hook.lock().unwrap().clone()
            {
                hook("prepared");
            }
        }
        Ok(())
    }
    #[cfg(all(test, feature = "service-temporary-charge-v1"))]
    pub(in super::super) fn battery_append_hook_for_tests(
        &self,
        hook: Arc<dyn Fn(&str) + Send + Sync>,
    ) {
        *self.battery_append_hook.lock().unwrap() = Some(hook);
    }
}

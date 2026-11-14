//! Battery request overrides follow managed process/user and source lifetimes.
//! Protected attempt rows retain requested/configured intent without converting
//! a temporary override into restart-persistent profiles or manual overrides.
use super::*;
use openblade_domain::battery_history::Desired;
fn cancellation_capacity(entry: &Entry) -> io::Result<usize> {
    let Some(t) = entry.tfc.as_ref().filter(|t| t.cancel_ids.is_some()) else {
        return Ok(0);
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
    Ok((serde_json::to_vec(&row)?.len() + 1) * 4)
}
impl Storage {
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
            let needed = row * 4
                + cancellation_capacity(&entry)?
                + if entry.tfc.as_ref().is_some_and(|t| t.origin.is_some()) {
                    0
                } else {
                    s.temporary_capacity
                }
                + retained;
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
        if entry.progress == 0 && entry.tfc.as_ref().is_some_and(|t| t.origin.is_some()) {
            s.temporary_capacity = 0;
        }
        if matches!(entry.progress, 72 | 73) {
            s.temporary_capacity = cancellation_capacity(&entry)?;
        }
        s.last = Some(entry);
        Ok(())
    }
}

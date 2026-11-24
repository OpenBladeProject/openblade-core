//! Managed `WindowsNvidiaGpuActivityReader` (OpenBlade.Platform): the NVIDIA
//! adapter's per-process GPU-engine utilization from Windows' WDDM counters.
//!
//! It reads the same counters as the GPU app inventory and never calls an
//! NVIDIA driver API, so a caller can decide from it whether NVIDIA's own API
//! may be used without waking an idle discrete GPU. The workload policy
//! (the sample window and which processes count) belongs to the caller.

use std::sync::atomic::AtomicBool;

use super::{
    BTreeMap, EngineCounter, NvidiaAdapterLuid, SamplingApi, Unavailable, counter_pid,
    native::{Query, WindowsSampling},
    read_counter_array,
};

/// One fresh `\GPU Engine(*)\Utilization Percentage` query for the NVIDIA
/// adapter. Dropping it closes the query.
pub struct NvidiaActivityQuery {
    api: WindowsSampling,
    query: Query,
    adapter: NvidiaAdapterLuid,
}

impl NvidiaActivityQuery {
    /// Managed `Refresh`: resolves the NVIDIA adapter's LUID through DXGI
    /// and opens a new wildcard counter query, so counter instances created
    /// since an earlier query are seen. Opening a DXGI factory and reading
    /// adapter descriptions creates no graphics device.
    pub fn open() -> Result<Self, Unavailable> {
        let mut api = WindowsSampling::default();
        let adapter = api.locate_adapter()?;
        let query = api.open_query()?;
        Ok(Self {
            api,
            query,
            adapter,
        })
    }

    /// Managed `Read`: collects the query and returns each NVIDIA process's
    /// summed utilization. A rate counter needs two collections, so the first
    /// read after `open` primes the query.
    pub fn read(&mut self) -> Result<BTreeMap<i32, f64>, Unavailable> {
        self.api.collect(&mut self.query)?;
        let counters = read_counter_array(&mut self.api, &mut self.query, &AtomicBool::new(false))?;
        Ok(nvidia_process_utilization(&counters, self.adapter))
    }
}

/// The summed utilization of each process on `adapter`, over the counters
/// with a valid status and a finite value, a negative value counting as zero
/// (managed `Read`'s projection).
fn nvidia_process_utilization(
    counters: &[EngineCounter],
    adapter: NvidiaAdapterLuid,
) -> BTreeMap<i32, f64> {
    let token = adapter.counter_token();
    let mut processes = BTreeMap::new();
    for counter in counters {
        if let Some(pid) = counter_pid(counter, &token) {
            *processes.entry(pid).or_insert(0.0) += counter.utilization_percent.max(0.0);
        }
    }
    processes
}

#[cfg(test)]
mod tests {
    use super::*;

    fn counter(instance: &str, status: u32, utilization_percent: f64) -> EngineCounter {
        EngineCounter {
            instance: instance.into(),
            status,
            utilization_percent,
        }
    }

    /// Managed sums every engine of a process on the NVIDIA adapter, skips
    /// other adapters, invalid statuses, non-finite values and unparsable
    /// instances, and counts a negative value as zero.
    #[test]
    fn sums_each_nvidia_process_as_managed_projects_it() {
        let adapter = NvidiaAdapterLuid {
            high: 0,
            low: 0x10f97,
        };
        let nvidia = "luid_0x00000000_0x00010f97";
        let counters = [
            counter(&format!("pid_4_{nvidia}_phys_0_eng_3_engtype_copy"), 0, 0.2),
            counter(
                &format!("pid_100_{nvidia}_phys_0_eng_0_engtype_3d"),
                1,
                0.05,
            ),
            counter(
                &format!(
                    "PID_100_{}_phys_0_eng_1_engtype_compute",
                    nvidia.to_uppercase()
                ),
                0,
                0.06,
            ),
            counter(
                &format!("pid_100_{nvidia}_phys_0_eng_2_engtype_copy"),
                0,
                -3.0,
            ),
            counter(
                "pid_200_luid_0x00000000_0x0000aaaa_phys_0_eng_0_engtype_3d",
                0,
                50.0,
            ),
            counter(
                &format!("pid_300_{nvidia}_phys_0_eng_0_engtype_3d"),
                2,
                50.0,
            ),
            counter(
                &format!("pid_301_{nvidia}_phys_0_eng_0_engtype_3d"),
                0,
                f64::NAN,
            ),
            counter(&format!("pid_0_{nvidia}_phys_0_eng_0_engtype_3d"), 0, 50.0),
            counter(&format!("pid_+5_{nvidia}_phys_0_eng_0_engtype_3d"), 0, 50.0),
            counter(&format!("{nvidia}_pid_6_phys_0"), 0, 50.0),
        ];
        let processes = nvidia_process_utilization(&counters, adapter);
        assert_eq!(processes.keys().copied().collect::<Vec<_>>(), [4, 100]);
        assert_eq!(processes[&4], 0.2);
        assert!(
            (processes[&100] - 0.11).abs() < 1e-12,
            "{}",
            processes[&100]
        );
    }
}

use super::{AudioEndpoint, Availability, FeatureAccess, State, Status};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Backend {
    V4,
    Legacy265,
}

#[derive(Clone, Copy)]
pub(super) struct Evidence {
    interactive_session: bool,
    exact_razer_host: bool,
    exact_usb_and_codec: bool,
    model: &'static str,
    pid: u16,
    product: &'static str,
    controllers_absent: bool,
    // A token stands for independently verified bound components, API and service.
    trusted_stack: Option<u32>,
    // Tokens stand for verified typed outputs, including exact ancestry/form factor.
    outputs: [Option<u32>; 2],
    active_output: AudioEndpoint,
    mutation_allowed: bool,
}

impl Evidence {
    pub(super) fn fixture(legacy: bool) -> Self {
        let (model, pid, product) = if legacy {
            ("RZ09-0482", 0x029D, "thx-2.6.5")
        } else {
            ("RZ09-0581", 0x02E0, "rznb12")
        };
        Self {
            interactive_session: true,
            exact_razer_host: true,
            exact_usb_and_codec: true,
            model,
            pid,
            product,
            controllers_absent: true,
            trusted_stack: Some(1),
            outputs: [Some(1), Some(2)],
            active_output: AudioEndpoint::Speakers,
            mutation_allowed: true,
        }
    }

    fn admit(self) -> Result<Binding, Refusal> {
        use Availability::*;
        let backend = match (self.model, self.pid, self.product) {
            ("RZ09-0581", 0x02E0, "rznb12")
            | ("RZ09-0528", 0x02C6, "rznb11")
            | ("RZ09-0530", 0x02C5, "rznb11") => Some(Backend::V4),
            ("RZ09-0482", 0x029D, "thx-2.6.5")
            | ("RZ09-0483", 0x029F, "thx-2.6.5")
            | ("RZ09-0484", 0x02A0, "thx-2.6.5") => Some(Backend::Legacy265),
            _ => None,
        };
        let Some(backend) = backend.filter(|_| {
            self.interactive_session && self.exact_razer_host && self.exact_usb_and_codec
        }) else {
            return Err(Refusal::unavailable(
                UnsupportedModel,
                "The exact synthetic THX host and codec could not be verified.",
            ));
        };
        if !self.controllers_absent {
            return Err(Refusal::unavailable(
                ControllerActive,
                "A competing synthetic audio controller is active.",
            ));
        }
        let Some(stack) = self.trusted_stack else {
            return Err(Refusal::unavailable(
                match backend {
                    Backend::V4 => ComponentsUnavailable,
                    Backend::Legacy265 => LegacyComponentsUnavailable,
                },
                "The synthetic THX components could not be verified.",
            ));
        };
        let mut outputs = self.outputs;
        if backend == Backend::Legacy265 {
            outputs[self.active_output.other().index()] = None;
        }
        Ok(Binding {
            backend,
            model: self.model,
            stack,
            outputs,
            mutation_allowed: self.mutation_allowed,
        })
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Binding {
    backend: Backend,
    model: &'static str,
    stack: u32,
    outputs: [Option<u32>; 2],
    mutation_allowed: bool,
}

impl Binding {
    fn availability(self) -> Availability {
        if self.mutation_allowed {
            Availability::Available
        } else {
            Availability::AvailableReadOnly
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub(super) struct Refusal {
    pub(super) status: Status,
    pub(super) availability: Availability,
    pub(super) error: &'static str,
}

impl Refusal {
    fn unavailable(availability: Availability, error: &'static str) -> Self {
        Self {
            status: Status::Unavailable,
            availability,
            error,
        }
    }
}

type ResultState = Result<(Availability, State), Refusal>;

#[derive(Clone, Copy)]
pub(super) enum Operation {
    Read,
    Connect,
    SetSpatial(bool),
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct Target {
    binding: Binding,
    endpoint: AudioEndpoint,
}

struct Reading {
    target: Target,
    spatial_enabled: bool,
}

enum Submission {
    Acknowledged,
    Uncertain,
}

trait Worker {
    fn read(&self, target: Target) -> Option<Reading>;
    async fn set_spatial(&mut self, target: Target, enabled: bool) -> Submission;
}

pub(super) struct FixtureWorker {
    binding: Option<Binding>,
    values: [bool; 2],
    uncertain_once: bool,
    #[cfg(test)]
    pending: bool,
    #[cfg(test)]
    submissions: usize,
}

impl FixtureWorker {
    pub(super) fn new(evidence: Evidence, uncertain_once: bool) -> Self {
        Self {
            binding: evidence.admit().ok(),
            values: [true, false],
            uncertain_once,
            #[cfg(test)]
            pending: false,
            #[cfg(test)]
            submissions: 0,
        }
    }
}

impl Worker for FixtureWorker {
    fn read(&self, target: Target) -> Option<Reading> {
        let binding = self.binding?;
        binding.outputs[target.endpoint.index()]?;
        Some(Reading {
            target: Target {
                binding,
                endpoint: target.endpoint,
            },
            spatial_enabled: self.values[target.endpoint.index()],
        })
    }

    async fn set_spatial(&mut self, target: Target, enabled: bool) -> Submission {
        self.values[target.endpoint.index()] = enabled;
        #[cfg(test)]
        {
            self.submissions += 1;
            if self.pending {
                return std::future::pending().await;
            }
        }
        if std::mem::take(&mut self.uncertain_once) {
            return Submission::Uncertain;
        }
        Submission::Acknowledged
    }
}

#[derive(Default)]
pub(super) struct Runtime {
    binding: Option<Binding>,
    confirmed: [bool; 2],
    probed: bool,
    paused: bool,
}

impl Runtime {
    pub(super) async fn fixture_dispatch(
        &mut self,
        operation: Operation,
        endpoint: AudioEndpoint,
        evidence: Evidence,
        worker: &mut FixtureWorker,
    ) -> ResultState {
        self.dispatch(operation, endpoint, || evidence, worker)
            .await
    }

    async fn dispatch(
        &mut self,
        operation: Operation,
        endpoint: AudioEndpoint,
        evidence: impl Fn() -> Evidence,
        worker: &mut impl Worker,
    ) -> ResultState {
        let binding = self.refresh(evidence())?;
        let availability = binding.availability();
        if self.paused && !matches!(operation, Operation::Read) {
            return Err(Refusal::unavailable(
                availability,
                "An explicit THX read is required after an uncertain write.",
            ));
        }
        let failed_read = Refusal {
            status: Status::Failed,
            availability,
            error: "The synthetic THX output could not be verified by readback.",
        };
        let spatial_enabled = match operation {
            Operation::Read | Operation::Connect => {
                // The first V4 read must probe both endpoints. Only the endpoint
                // explicitly requested here gets dispatcher confirmation.
                self.confirmed[endpoint.index()] = false;
                let value = if binding.backend == Backend::V4 && !self.probed {
                    let speakers =
                        Self::read(worker, binding, AudioEndpoint::Speakers).ok_or(failed_read)?;
                    let headphones = Self::read(worker, binding, AudioEndpoint::Headphones)
                        .ok_or(failed_read)?;
                    [speakers, headphones][endpoint.index()]
                } else {
                    Self::read(worker, binding, endpoint).ok_or(failed_read)?
                };
                self.require_current(binding, evidence())?;
                self.probed = true;
                self.paused = false;
                value
            }
            Operation::SetSpatial(enabled) => {
                if !binding.mutation_allowed || !self.confirmed[endpoint.index()] {
                    return Err(Refusal::unavailable(
                        availability,
                        "Read and admit this THX output before changing it.",
                    ));
                }
                self.require_current(binding, evidence())?;
                if Self::read(worker, binding, endpoint).is_none() {
                    self.confirmed[endpoint.index()] = false;
                    return Err(failed_read);
                }
                // Commit the pause before any possible submission. A dropped
                // future must retain it when the next pipe connection arrives.
                let previous_confirmed = self.confirmed;
                self.pause();
                let result = worker
                    .set_spatial(Target { binding, endpoint }, enabled)
                    .await;
                let readback = if matches!(result, Submission::Acknowledged) {
                    Self::read(worker, binding, endpoint)
                } else {
                    None
                };
                let current = self.refresh(evidence());
                if current.ok() != Some(binding) || readback != Some(enabled) {
                    return Err(Refusal {
                        status: Status::Indeterminate,
                        availability,
                        error: "The synthetic THX mutation could not be confirmed by readback.",
                    });
                }
                self.confirmed = previous_confirmed;
                self.paused = false;
                enabled
            }
        };
        self.confirmed[endpoint.index()] = true;
        Ok((
            availability,
            State::spatial(
                endpoint,
                spatial_enabled,
                match binding.backend {
                    Backend::V4 => FeatureAccess::Unavailable,
                    Backend::Legacy265 => FeatureAccess::ObservedOnly,
                },
            ),
        ))
    }

    fn read(worker: &impl Worker, binding: Binding, endpoint: AudioEndpoint) -> Option<bool> {
        binding.outputs[endpoint.index()]?;
        let target = Target { binding, endpoint };
        let reading = worker.read(target)?;
        (reading.target == target).then_some(reading.spatial_enabled)
    }

    fn refresh(&mut self, evidence: Evidence) -> Result<Binding, Refusal> {
        let next = evidence.admit();
        if self.binding != next.as_ref().ok().copied() {
            self.binding = next.as_ref().ok().copied();
            self.confirmed = [false; 2];
            self.probed = false;
        }
        next
    }

    fn require_current(&mut self, binding: Binding, evidence: Evidence) -> Result<(), Refusal> {
        if self.refresh(evidence)? != binding {
            return Err(Refusal::unavailable(
                Availability::ComponentsUnavailable,
                "The synthetic THX runtime or output changed. Read again.",
            ));
        }
        Ok(())
    }

    pub(super) fn pause(&mut self) {
        self.confirmed = [false; 2];
        self.paused = true;
    }
}

#[cfg(test)]
mod tests;

# Keep Custom getter admission separate from saved settings

The exact RZ09-0581 0D/87 getter admits Low through High; the general saved
CustomPerformanceSettings schema also permits CPU Boost. High/High admission
belongs to the exact getter, not that generic schema. Automatic High/High
rejects and poisons; Manual High/High confirms Custom/Maximum with unknown
levels and leaves the managed port usable for guarded Automatic handback.
Never infer MaximumFans=true from manual observation or retain older levels
inside a newly confirmed unknown result.

Portable query geometry and complete observations live in the shared protocol,
types and domain crates. Windows custody, native cancellation and retirement
remain in the existing installed Reader/Worker composition. Managed queued GPU
cancellation does not poison, while claimed cancellation does even if the
inner session is Healthy. The native worker has no component queue: it owns
one whole claimed observation and always retires its private Reader before
returning. Test invocation, actual submission and retirement independently.

The conservative process-wide allocator still admits 127 pair reservations,
not 127 guaranteed results. Custom reserves a second pair only after the
selector pair: at most 63 complete Custom observations; canceled reservations
can reduce that number. Exhaustion never refreshes retained presentation, which
expires at five seconds. New handles and completed GETs do not prove firmware
mailbox drain or authorize transaction reuse. This offline slice establishes
no runtime, mutation authority or full-backend cutover readiness.

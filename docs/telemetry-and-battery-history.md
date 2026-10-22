# Telemetry and battery history

Open **Telemetry** to inspect battery power, CPU/GPU temperatures or either fan's
speed. Choose a metric and time range. The chart shows minimum, maximum and
average values from the original readings; drawing fewer points does not change
the statistics. The horizontal axis shows actual timestamps across the selected
time range, including empty spans before or after the available readings. Missing
readings and pauses in observation remain gaps.

Hover or touch the chart to inspect a moment. A vertical crosshair selects the
nearest original reading, and the readout shows its timestamp and all available
metrics together. Use **Previous sample** and **Next sample** with the keyboard
for the same inspection. The selected moment stays fixed as new readings arrive;
choose **Follow latest** to return to the newest reading.

The page identifies live data, stale data and local CSV replay. Power-source and
active-profile changes appear below the chart. Available sensors depend on the
laptop and the current control state.

## Record and replay

Choose **Record CSV** and select a local destination to start recording. The
existing observation loop requests the telemetry sensors while the page is open
or a recording is active. Explicit recording works when the normal live-readings
preference is off and continues when the window is hidden. Exiting the Tray
finalizes the recording; closing only its window keeps it running.

Choose **Stop recording** to drain accepted readings and finalize the CSV.
Recordings are bounded to 24 hours and 86,400 readings, with bounded queue and
file limits. During recording the writer uses a `.partial` file beside the
destination and publishes the completed CSV atomically. A finalization failure
shows the recoverable partial path and allows a new recording.

Choose **Open CSV replay** to inspect an OpenBlade recording. Replay preserves
its recorded source and timestamps, marks the view as local replay, and leaves
live history separate. **Return to live** resumes the live view. Recordings stay
on the selected computer; OpenBlade does not upload them.

## Battery history and preset

Open **Laptop > Battery history** and choose **Load battery history**. The
current-user agent asks Windows `powercfg /batteryreport` for the last 24 hours.
The timeline distinguishes AC, battery, active, standby, suspended and unknown
periods. The interval list includes the charge and discharged energy Windows
reported. Missing activity or energy remains unknown. This is Windows' historical
report, separate from OpenBlade's live telemetry.

The summary shows design capacity, full-charge capacity, health and cycle count
when Windows provides them. The report's generation time identifies its age.

Choose **Preview preset** under **Long battery life** to inspect changes before
saving them to the Battery profile. The preset selects supported lower-power
performance, Automatic fans, lighting Off, the lowest recognized fixed refresh
rate and a usable reported brightness level nearest 35%. Unavailable settings remain
unchanged. The preview shows each changed value; saving preserves the other
power-source profiles and existing manual or application override priority.

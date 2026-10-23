# Telemetry and battery history

Open **Telemetry** to compare battery power, CPU/GPU temperatures and fan speed.
Temperature, power and fan speed use separate scales with a shared time axis.
Use the **Metrics and colors** gear to choose visible sensors and their colors; these
preferences are saved for your Windows user. Missing readings and pauses remain
gaps. Line patterns help distinguish paired sensors alongside their colors.

Hover over a chart to inspect the nearest original sample. The vertical
crosshair, on-chart value callouts and **At sample** column show the same original
sample across every metric. Callouts include the sample time and stay inside the
plot at either edge.
Click to pin it, or use the sample arrows (**Previous sample** and **Next sample**)
beside the timestamp with the keyboard.
A pinned moment and its time window stay fixed as readings arrive. Choose
**Follow latest** to follow the newest readings again. Turn it off to hold the
visible window while browsing samples. If a pinned reading ages
out of the retained history, the chart clears it and says so. Hover, use
**Next sample**, or choose **Follow latest** to inspect available readings.

The analysis table shows each metric's minimum, average, maximum and available
reading count within the visible range. Counts expose missing readings in their
tooltips and accessible labels. All statistics use the original readings; drawing
fewer points does not change them. On a narrow window, expand **Range statistics**
for the minimum, average and maximum.

Drag either edge of the overview to resize the visible range, or drag its center
to move it. The start and end fields allow precise keyboard edits, including
fractional seconds and a UTC offset. The chart keeps the selected time bounds,
including empty spans before or after readings. Edited ranges stay fixed until
you choose **Follow latest** or use the timeline
reset tool (**Full range**). If older readings expire, the
chart shows the available range and explains the adjustment. Starting or stopping
a CSV recording keeps the current selection.

The page identifies live data, stale data and local CSV replay. Power-source and
active-profile changes appear below the chart. Available sensors depend on the
laptop and the current control state.

## Record and replay

Choose **Record CSV** and select a local destination to start recording. The
existing observation loop requests the telemetry sensors while the page is open
or a recording is active. Explicit recording works when the normal live-readings
preference is off and continues when the window is hidden. Exiting the Tray
finalizes the recording; closing only its window keeps it running.

The recording action becomes **Stop recording** while a CSV is active. Choose it
to drain accepted readings and finalize the CSV.
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

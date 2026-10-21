# Application profiles

Open **Profiles > Application profiles** to assign settings to a game or app.
Choose **Add application** and browse to its `.exe`, or choose **Discover installed games**
to review suggestions from Steam, Epic Games, Xbox and Battle.net. Discovery reads
local installation metadata. Review the executable path before saving; a launcher
and the game it starts are different processes.

Enter a name and choose an activation mode:

- **While in foreground** applies the rule while that exact executable owns the
  foreground window.
- **Until the activated process exits** keeps it active after switching windows.
  If several such processes remain open, the most recently activated one wins.
  A matching foreground rule takes priority.

Choose **Plugged in**, **Battery**, or **USB-C** to edit that source's overrides.
Unset fields inherit the corresponding power-source profile. You can override
performance, fans, lighting, display settings and Gaming mode. Use **Copy saved
controls from this power profile** to reuse its custom CPU/GPU settings, fan
curve and complete lighting effect, then change or unset individual controls.

Display overrides include refresh rate, HDR, color profile and brightness. An
HDR-only rule leaves the other fields inherited. Brightness uses the internal
display's reported levels and selects the closest available level, choosing the
lower one when two levels are equally close.

Manual controls keep priority over application profiles. Application automation
runs in the Session Agent, so closing the visible Tray window does not stop it.
Display automation saves the state it takes ownership of and restores it when
the rule ends. A possibly submitted but unconfirmed display change pauses
automation until the state can be reconciled.

Disabling or deleting the active rule returns control to the remaining profile.
If a game moves to another folder, update its exact executable path. Profile
import and export include application rules and per-source brightness. Installing
this feature requires matching Service, Session Agent and Tray versions.

## Implementation

The current-user agent observes foreground executables and holds process handles
for activated while-running rules. It reports sequenced context through typed
service IPC; the service binds it to the admitted interactive session and expires
unrenewed context. Existing typed capability and hardware admission still govern
every requested control. An application rule grants no new model support or
firmware command.

Persisted profiles use schema 22, exported profiles use schema 14, service IPC is
71 and display IPC is 2. Older profiles migrate with no application rules and no
brightness overrides.
